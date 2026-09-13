namespace FiGet.Domain.Entities;

/// <summary>
/// A file or folder in an asset directory. Folders are rows too, because a folder can exist empty: the
/// API creates them explicitly, and a listing has to show one before anything is uploaded into it.
/// </summary>
public sealed class AssetItem
{
    public int Key { get; set; }

    public int FeedKey { get; set; }

    public Feed? Feed { get; set; }

    /// <summary>The full path as it was first written, for example <c>tools/dotnet/hosting.exe</c>.</summary>
    public required string Path { get; set; }

    /// <summary>Lookup key. Paths are case-insensitive, like feed names and package ids.</summary>
    public required string PathLower { get; set; }

    /// <summary>The lower-cased path of the containing folder; empty in the root.</summary>
    public required string ParentLower { get; set; }

    /// <summary>The last segment of <see cref="Path"/>.</summary>
    public required string Name { get; set; }

    public bool IsDirectory { get; set; }

    /// <summary>
    /// Where the bytes are kept. Random rather than derived from the path, so that a name never becomes a
    /// file-system path and an overwrite can write the new file before the old one is removed. Null on a folder.
    /// </summary>
    public string? BlobId { get; set; }

    public long Size { get; set; }

    public string? ContentType { get; set; }

    public string? Md5 { get; set; }

    public string? Sha1 { get; set; }

    public string? Sha256 { get; set; }

    public string? Sha512 { get; set; }

    /// <summary>User-defined metadata as JSON: <c>{"key": {"value": "...", "includeInResponseHeader": false}}</c>.</summary>
    public string? UserMetadata { get; set; }

    /// <summary>The cache header type the API set, for example <c>ttl</c>; null when none was set.</summary>
    public string? CacheHeaderType { get; set; }

    public string? CacheHeaderValue { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ModifiedUtc { get; set; }
}
