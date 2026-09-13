using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiGet.Application.Ports;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FiGet.Application.Assets;

/// <summary>How an upload treats a file that is already there. Named after the three verbs that select them.</summary>
public enum AssetWriteMode
{
    /// <summary><c>PUT</c>: store only when nothing is at the path yet.</summary>
    CreateOnly,

    /// <summary><c>POST</c>: store, replacing whatever file is at the path.</summary>
    CreateOrReplace,

    /// <summary><c>PATCH</c>: store only when a file is already at the path.</summary>
    ReplaceOnly,
}

public enum AssetOutcome
{
    Created,
    Replaced,
    Deleted,
    Updated,

    /// <summary>A create-only write, or a folder create where a folder already is.</summary>
    AlreadyExists,
    NotFound,

    /// <summary>The path names a folder where a file was expected, or the other way round.</summary>
    WrongType,

    /// <summary>Something above the path is a file, so the path cannot exist.</summary>
    ParentIsFile,
    NotEmpty,
    InvalidPath,
    TooLarge,

    /// <summary>A multipart request whose numbers do not add up, or a completion with parts missing.</summary>
    InvalidUpload,
}

/// <summary>One user-defined metadata value, as the asset API spells it.</summary>
public sealed record AssetUserMetadataValue(string Value, bool IncludeInResponseHeader);

/// <summary>A metadata change. Every part is optional; what is null is left alone.</summary>
public sealed record AssetMetadataChange(
    string? ContentType,
    IReadOnlyDictionary<string, AssetUserMetadataValue>? UserMetadata,
    bool ReplaceUserMetadata,
    string? CacheHeaderType,
    string? CacheHeaderValue);

/// <summary>
/// What an asset directory does: store a file at a path, make a folder, delete, describe. The rules live
/// here rather than in the endpoints so the API and the upload page cannot drift apart.
/// </summary>
public sealed class AssetService(IAssetStore store, IAssetStorage storage, TimeProvider time, ILogger<AssetService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AssetItem?> FindAsync(Feed feed, AssetPath path, CancellationToken cancellationToken) =>
        path.IsRoot ? null : await store.FindAsync(feed.Key, path.Lower, cancellationToken);

    public Task<IReadOnlyList<AssetItem>> ListAsync(Feed feed, AssetPath folder, bool recursive, CancellationToken cancellationToken) =>
        store.ListAsync(feed.Key, folder.Lower, recursive, cancellationToken);

    public Task<Stream?> OpenAsync(Feed feed, AssetItem file, CancellationToken cancellationToken) =>
        file.BlobId is null
            ? Task.FromResult<Stream?>(null)
            : storage.OpenAsync(new AssetBlobKey(feed.NameLower, file.BlobId), cancellationToken);

    /// <summary>
    /// Stores <paramref name="content"/> at <paramref name="path"/>, creating the folders above it. The body
    /// is only read once the mode allows the write, so a refused create-only upload costs nothing.
    /// </summary>
    public async Task<AssetOutcome> WriteAsync(
        Feed feed,
        AssetPath path,
        Stream content,
        string contentType,
        AssetWriteMode mode,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return AssetOutcome.InvalidPath;
        }

        var existing = await store.FindAsync(feed.Key, path.Lower, cancellationToken);
        if (existing?.IsDirectory == true)
        {
            return AssetOutcome.WrongType;
        }

        if (existing is not null && mode == AssetWriteMode.CreateOnly)
        {
            return AssetOutcome.AlreadyExists;
        }

        if (existing is null && mode == AssetWriteMode.ReplaceOnly)
        {
            return AssetOutcome.NotFound;
        }

        if (await BlockedByFileAsync(feed, path, cancellationToken))
        {
            return AssetOutcome.ParentIsFile;
        }

        var blob = new AssetBlobKey(feed.NameLower, Guid.NewGuid().ToString("N"));
        AssetHashes hashes;
        try
        {
            await using var hashed = new AssetHashingStream(content, maxBytes);
            await storage.SaveAsync(blob, hashed, cancellationToken);
            hashes = hashed.Finish();
        }
        catch (AssetTooLargeException)
        {
            // Storage writes to a temporary file and moves it into place, so a refused upload left nothing.
            return AssetOutcome.TooLarge;
        }

        var now = time.GetUtcNow().UtcDateTime;
        await EnsureFoldersAsync(feed, path.Ancestors(), now, cancellationToken);

        if (existing is null)
        {
            var created = new AssetItem
            {
                FeedKey = feed.Key,
                Path = path.Value,
                PathLower = path.Lower,
                ParentLower = path.Parent.Lower,
                Name = path.Name,
                CreatedUtc = now,
            };
            Apply(created, blob.BlobId, hashes, contentType, now);
            if (await store.AddAsync(created, cancellationToken))
            {
                return AssetOutcome.Created;
            }

            // Somebody else stored this path between the check and the insert.
            existing = await store.FindAsync(feed.Key, path.Lower, cancellationToken);
            if (existing is null || existing.IsDirectory || mode == AssetWriteMode.CreateOnly)
            {
                await storage.DeleteAsync(blob, cancellationToken);
                return existing?.IsDirectory == true ? AssetOutcome.WrongType : AssetOutcome.AlreadyExists;
            }
        }

        // New bytes first, row second, old bytes last: at every moment the row points at a complete file.
        var previous = existing.BlobId;
        Apply(existing, blob.BlobId, hashes, contentType, now);
        await store.UpdateAsync(existing, cancellationToken);
        if (previous is not null)
        {
            await storage.DeleteAsync(new AssetBlobKey(feed.NameLower, previous), cancellationToken);
        }

        return AssetOutcome.Replaced;
    }

    /// <summary>
    /// Stores one part of a multipart upload. Nothing appears in the directory until
    /// <see cref="CompleteUploadAsync"/>; until then the parts wait on shared storage, and ones nobody completes
    /// are swept away after a while.
    /// </summary>
    public async Task<AssetOutcome> UploadPartAsync(
        Feed feed,
        string uploadId,
        int index,
        long offset,
        long totalSize,
        long partSize,
        int totalParts,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(uploadId)
            || totalParts <= 0
            || index < 0
            || index >= totalParts
            || offset < 0
            || partSize <= 0
            || totalSize <= 0
            || offset + partSize > totalSize)
        {
            return AssetOutcome.InvalidUpload;
        }

        // Refused on the first part rather than on completion, so nobody uploads four gigabytes to be told no.
        if (totalSize > maxBytes)
        {
            return AssetOutcome.TooLarge;
        }

        var key = UploadKey(feed, uploadId);
        var counted = new CountingStream(content, partSize);
        try
        {
            await storage.SaveUploadPartAsync(key, index, offset, counted, cancellationToken);
        }
        catch (AssetTooLargeException)
        {
            return AssetOutcome.InvalidUpload;
        }

        if (counted.Count != partSize)
        {
            // The part is stored, but it is not the size its request claimed; completion would find the gap.
            return AssetOutcome.InvalidUpload;
        }

        await storage.SaveUploadManifestAsync(key, totalSize, totalParts, cancellationToken);
        return AssetOutcome.Updated;
    }

    /// <summary>
    /// Joins the parts of an upload into the file at <paramref name="path"/>, replacing one that is there, as
    /// an ordinary upload by POST does. The parts must be every index once, end to end, adding up to the
    /// total the upload announced; otherwise nothing is stored and the parts stay for a corrected retry.
    /// </summary>
    public async Task<AssetOutcome> CompleteUploadAsync(
        Feed feed,
        AssetPath path,
        string uploadId,
        string contentType,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return AssetOutcome.InvalidUpload;
        }

        var key = UploadKey(feed, uploadId);
        if (await storage.ReadUploadManifestAsync(key, cancellationToken) is not { } manifest)
        {
            return AssetOutcome.InvalidUpload;
        }

        var parts = await storage.ListUploadPartsAsync(key, cancellationToken);
        long expectedOffset = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Index != i || parts[i].Offset != expectedOffset)
            {
                return AssetOutcome.InvalidUpload;
            }

            expectedOffset += parts[i].Size;
        }

        if (parts.Count != manifest.TotalParts || expectedOffset != manifest.TotalSize)
        {
            return AssetOutcome.InvalidUpload;
        }

        var sources = parts
            .Select(part => (Func<CancellationToken, Task<Stream>>)(async token =>
                await storage.OpenUploadPartAsync(key, part, token)
                    ?? throw new IOException($"Part {part.Index} of an upload disappeared while it was being completed.")))
            .ToList();

        AssetOutcome outcome;
        await using (var joined = new ConcatenatedStream(sources))
        {
            outcome = await WriteAsync(feed, path, joined, contentType, AssetWriteMode.CreateOrReplace, maxBytes, cancellationToken);
        }

        if (outcome is AssetOutcome.Created or AssetOutcome.Replaced)
        {
            await storage.DeleteUploadAsync(key, cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// The client chooses the id and the API promises nothing about its shape - a GUID is only one option -
    /// so it is hashed into a fixed storage-safe form rather than validated.
    /// </summary>
    private static AssetUploadKey UploadKey(Feed feed, string uploadId) =>
        new(feed.NameLower, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uploadId)))[..32]);

    /// <summary>Creates a folder and the folders above it. Creating one that exists is not an error.</summary>
    public async Task<AssetOutcome> CreateFolderAsync(Feed feed, AssetPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return AssetOutcome.AlreadyExists;
        }

        var existing = await store.FindAsync(feed.Key, path.Lower, cancellationToken);
        if (existing is not null)
        {
            return existing.IsDirectory ? AssetOutcome.AlreadyExists : AssetOutcome.WrongType;
        }

        if (await BlockedByFileAsync(feed, path, cancellationToken))
        {
            return AssetOutcome.ParentIsFile;
        }

        await EnsureFoldersAsync(feed, [.. path.Ancestors(), path], time.GetUtcNow().UtcDateTime, cancellationToken);
        return AssetOutcome.Created;
    }

    /// <summary>
    /// Deletes a file, or a folder: an empty one always, a full one only when <paramref name="recursive"/>
    /// says so. The rows go first, then the bytes, so nothing is ever listed that cannot be downloaded.
    /// </summary>
    public async Task<AssetOutcome> DeleteAsync(Feed feed, AssetPath path, bool recursive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return AssetOutcome.InvalidPath;
        }

        var existing = await store.FindAsync(feed.Key, path.Lower, cancellationToken);
        if (existing is null)
        {
            return AssetOutcome.NotFound;
        }

        if (existing.IsDirectory && !recursive && await store.HasChildrenAsync(feed.Key, path.Lower, cancellationToken))
        {
            return AssetOutcome.NotEmpty;
        }

        var blobs = await store.DeleteAsync(feed.Key, path.Lower, cancellationToken);
        foreach (var blob in blobs)
        {
            try
            {
                await storage.DeleteAsync(new AssetBlobKey(feed.NameLower, blob), cancellationToken);
            }
            catch (IOException ex)
            {
                // The row is gone, so the file can no longer be reached; a leftover file wastes space and
                // nothing else. Logged, because that space is only recoverable by somebody who knows.
                logger.LogWarning(ex, "Could not remove the stored file {Blob} of asset directory {Feed}.", blob, feed.Name);
            }
        }

        return AssetOutcome.Deleted;
    }

    public async Task<AssetOutcome> UpdateMetadataAsync(Feed feed, AssetPath path, AssetMetadataChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(change);
        var existing = path.IsRoot ? null : await store.FindAsync(feed.Key, path.Lower, cancellationToken);
        if (existing is null)
        {
            return AssetOutcome.NotFound;
        }

        // A folder has no content type of its own; the API ignores one sent for it rather than refusing.
        if (!existing.IsDirectory && !string.IsNullOrWhiteSpace(change.ContentType))
        {
            existing.ContentType = change.ContentType.Trim();
        }

        if (change.UserMetadata is not null)
        {
            var merged = change.ReplaceUserMetadata
                ? new Dictionary<string, AssetUserMetadataValue>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AssetUserMetadataValue>(ReadUserMetadata(existing), StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in change.UserMetadata)
            {
                merged[key] = value;
            }

            existing.UserMetadata = merged.Count == 0 ? null : JsonSerializer.Serialize(merged, Json);
        }

        if (change.CacheHeaderType is not null)
        {
            existing.CacheHeaderType = change.CacheHeaderType.Trim().Length == 0 ? null : change.CacheHeaderType.Trim();
            existing.CacheHeaderValue = existing.CacheHeaderType is null ? null : change.CacheHeaderValue;
        }

        existing.ModifiedUtc = time.GetUtcNow().UtcDateTime;
        await store.UpdateAsync(existing, cancellationToken);
        return AssetOutcome.Updated;
    }

    /// <summary>The user-defined metadata of an item; empty when there is none or it cannot be read.</summary>
    public static IReadOnlyDictionary<string, AssetUserMetadataValue> ReadUserMetadata(AssetItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.UserMetadata))
        {
            return new Dictionary<string, AssetUserMetadataValue>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, AssetUserMetadataValue>>(item.UserMetadata, Json)
                ?? new Dictionary<string, AssetUserMetadataValue>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, AssetUserMetadataValue>();
        }
    }

    private static void Apply(AssetItem item, string blobId, AssetHashes hashes, string contentType, DateTime now)
    {
        item.IsDirectory = false;
        item.BlobId = blobId;
        item.Size = hashes.Size;
        item.ContentType = contentType;
        item.Md5 = hashes.Md5;
        item.Sha1 = hashes.Sha1;
        item.Sha256 = hashes.Sha256;
        item.Sha512 = hashes.Sha512;
        item.ModifiedUtc = now;
    }

    /// <summary>Whether a file sits where one of the folders above this path would have to be.</summary>
    private async Task<bool> BlockedByFileAsync(Feed feed, AssetPath path, CancellationToken cancellationToken)
    {
        foreach (var ancestor in path.Ancestors())
        {
            var item = await store.FindAsync(feed.Key, ancestor.Lower, cancellationToken);
            if (item is { IsDirectory: false })
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnsureFoldersAsync(Feed feed, IEnumerable<AssetPath> folders, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var folder in folders)
        {
            if (await store.FindAsync(feed.Key, folder.Lower, cancellationToken) is not null)
            {
                continue;
            }

            // A false here means a parallel upload made the same folder first, which is what was wanted.
            await store.AddAsync(
                new AssetItem
                {
                    FeedKey = feed.Key,
                    Path = folder.Value,
                    PathLower = folder.Lower,
                    ParentLower = folder.Parent.Lower,
                    Name = folder.Name,
                    IsDirectory = true,
                    CreatedUtc = now,
                    ModifiedUtc = now,
                },
                cancellationToken);
        }
    }
}
