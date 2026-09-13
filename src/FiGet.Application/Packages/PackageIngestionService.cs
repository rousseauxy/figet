using System.IO.Compression;
using System.Reflection.Metadata;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Packages;
using Microsoft.Extensions.Logging;

namespace FiGet.Application.Packages;

public enum PushOutcome
{
    Created,
    Replaced,
    Conflict,
    Invalid,
    NotFound,
}

public sealed record PushResult(PushOutcome Outcome, string Message, string? Id = null, string? Version = null);

/// <summary>Accepts pushed packages and symbol packages, and deletes versions.</summary>
public sealed class PackageIngestionService(
    IPackageIndexer indexer,
    IPackageStorage storage,
    IPackageStore store,
    TimeProvider time,
    ILogger<PackageIngestionService> logger)
{
    private const string SymbolsPackageType = "SymbolsPackage";

    /// <summary>Largest single PDB accepted from a symbol package.</summary>
    private const long MaxPdbSize = 512L * 1024 * 1024;

    /// <summary>
    /// Indexes and stores a pushed package. The metadata row is written first, so a concurrent push of the
    /// same version loses on the database's unique index instead of silently replacing the file; the file
    /// is written second, and the row is removed again if that fails.
    /// </summary>
    public Task<PushResult> PushAsync(Feed feed, Stream nupkg, CancellationToken cancellationToken) =>
        PushAsync(feed, nupkg, PackageOrigin.Pushed, cancellationToken);

    /// <summary>
    /// The same, for a package that came from an upstream rather than from a client. A cached package is
    /// identical in every way except its origin, which is what retention and cache pruning act on - and its
    /// publish date, when the upstream said when it published it. A nupkg does not carry one, so without it
    /// a cached copy would read as published the moment somebody first installed it.
    /// </summary>
    public async Task<PushResult> PushAsync(Feed feed, Stream nupkg, PackageOrigin origin, CancellationToken cancellationToken, DateTime? publishedUtc = null)
    {
        IndexedPackage indexed;
        try
        {
            indexed = await indexer.IndexAsync(nupkg, cancellationToken);
        }
        catch (InvalidPackageException ex)
        {
            return new PushResult(PushOutcome.Invalid, ex.Message);
        }

        var normalized = indexed.Version.ToNormalizedString();
        if (indexed.PackageTypes.Contains(SymbolsPackageType, StringComparer.OrdinalIgnoreCase))
        {
            return new PushResult(PushOutcome.Invalid, "Symbol packages must be pushed to the symbol publish resource.", indexed.Id, normalized);
        }

        var idLower = indexed.Id.ToLowerInvariant();
        var versionLower = normalized.ToLowerInvariant();
        var existed = await store.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken) is not null;
        if (existed && !feed.AllowOverwrite)
        {
            return new PushResult(PushOutcome.Conflict, $"{indexed.Id} {normalized} already exists in feed '{feed.Name}'.", indexed.Id, normalized);
        }

        var row = ToEntity(indexed, publishedUtc ?? time.GetUtcNow().UtcDateTime, origin);
        row.LastUsedUtc = time.GetUtcNow().UtcDateTime;
        if (!await store.AddVersionAsync(feed.Key, indexed.Id, row, feed.AllowOverwrite, cancellationToken))
        {
            return new PushResult(PushOutcome.Conflict, $"{indexed.Id} {normalized} already exists in feed '{feed.Name}'.", indexed.Id, normalized);
        }

        var key = new PackageStorageKey(feed.Key, idLower, versionLower);
        try
        {
            nupkg.Position = 0;
            await storage.SavePackageAsync(key, nupkg, indexed.Nuspec, overwrite: true, cancellationToken);
        }
        catch
        {
            if (!existed)
            {
                await store.DeleteVersionAsync(feed.Key, idLower, versionLower, CancellationToken.None);
            }

            throw;
        }

        return new PushResult(existed ? PushOutcome.Replaced : PushOutcome.Created, $"{indexed.Id} {normalized} stored.", indexed.Id, normalized);
    }

    /// <summary>
    /// Accepts a .snupkg for a version that already exists in the feed. Every PDB must be a portable PDB;
    /// each is stored under its symbol server key.
    /// </summary>
    public async Task<PushResult> PushSymbolsAsync(Feed feed, Stream snupkg, CancellationToken cancellationToken)
    {
        IndexedPackage indexed;
        try
        {
            indexed = await indexer.IndexAsync(snupkg, cancellationToken);
        }
        catch (InvalidPackageException ex)
        {
            return new PushResult(PushOutcome.Invalid, ex.Message);
        }

        var normalized = indexed.Version.ToNormalizedString();
        if (!indexed.PackageTypes.Contains(SymbolsPackageType, StringComparer.OrdinalIgnoreCase))
        {
            return new PushResult(PushOutcome.Invalid, "The file is not a symbol package (package type SymbolsPackage).", indexed.Id, normalized);
        }

        var version = await store.GetVersionAsync(feed.Key, indexed.Id.ToLowerInvariant(), normalized.ToLowerInvariant(), cancellationToken);
        if (version is null)
        {
            return new PushResult(PushOutcome.NotFound, $"{indexed.Id} {normalized} must be pushed before its symbols.", indexed.Id, normalized);
        }

        var pdbs = new List<(SymbolFile Row, byte[] Content)>();
        snupkg.Position = 0;
        using (var archive = new ZipArchive(snupkg, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.Length > MaxPdbSize)
                {
                    return new PushResult(PushOutcome.Invalid, $"{entry.FullName} is larger than the symbol size limit.", indexed.Id, normalized);
                }

                var content = new byte[entry.Length];
                await using (var stream = await entry.OpenAsync(cancellationToken))
                {
                    await stream.ReadExactlyAsync(content, cancellationToken);
                }

                var symbolKey = TryReadPortablePdbKey(content);
                if (symbolKey is null)
                {
                    return new PushResult(PushOutcome.Invalid, $"{entry.FullName} is not a portable PDB; only portable PDBs are accepted.", indexed.Id, normalized);
                }

                pdbs.Add((new SymbolFile
                {
                    FeedKey = feed.Key,
                    PackageVersionKey = version.Key,
                    FileNameLower = entry.Name.ToLowerInvariant(),
                    SymbolKeyLower = symbolKey,
                    Size = content.LongLength,
                }, content));
            }
        }

        if (pdbs.Count == 0)
        {
            return new PushResult(PushOutcome.Invalid, "The symbol package contains no PDB files.", indexed.Id, normalized);
        }

        foreach (var (row, content) in pdbs)
        {
            using var stream = new MemoryStream(content, writable: false);
            await storage.SaveSymbolAsync(new SymbolStorageKey(feed.Key, row.FileNameLower, row.SymbolKeyLower), stream, cancellationToken);
        }

        await store.ReplaceSymbolFilesAsync(version.Key, pdbs.Select(p => p.Row).ToList(), cancellationToken);
        return new PushResult(PushOutcome.Created, $"Symbols for {indexed.Id} {normalized} stored.", indexed.Id, normalized);
    }

    /// <summary>Unlists or hard-deletes a version, following the feed's deletion behaviour.</summary>
    public async Task<bool> DeleteAsync(Feed feed, string id, string version, CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();
        var versionLower = NormalizeLower(version);
        if (versionLower is null)
        {
            return false;
        }

        if (feed.DeletionBehavior == PackageDeletionBehavior.Unlist)
        {
            return await store.SetListedAsync(feed.Key, idLower, versionLower, listed: false, cancellationToken);
        }

        return await PurgeAsync(feed, id, version, cancellationToken);
    }

    /// <summary>
    /// Removes a version and its files outright, whatever the feed's delete behaviour says.
    /// <see cref="DeleteAsync"/> honours that setting, so on a feed that unlists it would only unlist
    /// again - no answer at all when the caller is looking at a version that is already unlisted.
    /// </summary>
    /// <summary>
    /// Forgets every copy this feed cached of one package, so it follows its upstreams again. Versions
    /// pushed here are left alone: what somebody published to this feed is nobody else's to remove, the
    /// same rule withdrawal reconciliation follows.
    ///
    /// It matters because a cached copy wins the merge. Local beats upstream by design, so one cached
    /// version keeps being answered - and keeps being latest - however the gallery has moved on, until
    /// somebody removes it. Nothing is lost: the next download fetches it again.
    /// </summary>
    /// <returns>How many versions were removed.</returns>
    public async Task<int> UncacheAsync(Feed feed, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var idLower = id.ToLowerInvariant();
        var package = await store.GetPackageAsync(feed.Key, idLower, includeDependencies: false, cancellationToken);
        if (package is null)
        {
            return 0;
        }

        // Materialised first: PurgeAsync deletes rows, and the last one takes the package with it.
        var cached = package.Versions
            .Where(v => v.Origin == PackageOrigin.Cached)
            .Select(v => v.NormalizedVersion)
            .ToList();

        var removed = 0;
        foreach (var version in cached)
        {
            if (await PurgeAsync(feed, id, version, cancellationToken))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            logger.LogInformation(
                "Un-cached {Count} version(s) of {Id} from feed {Feed}; it follows its upstreams again.",
                removed,
                package.Id,
                feed.Name);
        }

        return removed;
    }

    public async Task<bool> PurgeAsync(Feed feed, string id, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var idLower = id.ToLowerInvariant();
        var versionLower = NormalizeLower(version);
        if (versionLower is null)
        {
            return false;
        }

        var row = await store.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken);
        if (row is null)
        {
            return false;
        }

        var symbols = await store.GetSymbolFilesAsync(row.Key, cancellationToken);
        if (!await store.DeleteVersionAsync(feed.Key, idLower, versionLower, cancellationToken))
        {
            return false;
        }

        await storage.DeletePackageAsync(new PackageStorageKey(feed.Key, idLower, versionLower), cancellationToken);
        foreach (var symbol in symbols)
        {
            await storage.DeleteSymbolAsync(new SymbolStorageKey(feed.Key, symbol.FileNameLower, symbol.SymbolKeyLower), cancellationToken);
        }

        return true;
    }

    /// <summary>Hides a version from listings and search, whatever the feed's delete behaviour says.</summary>
    public async Task<bool> UnlistAsync(Feed feed, string id, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var versionLower = NormalizeLower(version);
        return versionLower is not null
            && await store.SetListedAsync(feed.Key, id.ToLowerInvariant(), versionLower, listed: false, cancellationToken);
    }

    public async Task<bool> RelistAsync(Feed feed, string id, string version, CancellationToken cancellationToken)
    {
        var versionLower = NormalizeLower(version);
        return versionLower is not null
            && await store.SetListedAsync(feed.Key, id.ToLowerInvariant(), versionLower, listed: true, cancellationToken);
    }

    /// <summary>Normalises a version from a URL; null when it does not parse.</summary>
    public static string? NormalizeLower(string version) =>
        NuGet.Versioning.NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString().ToLowerInvariant() : null;

    private static PackageVersion ToEntity(IndexedPackage p, DateTime publishedUtc, PackageOrigin origin)
    {
        var normalized = p.Version.ToNormalizedString();
        var tagsLower = p.Tags.ToLowerInvariant();
        var dependencies = new List<PackageDependency>();
        var ordinal = 0;
        foreach (var group in p.DependencyGroups)
        {
            if (group.Dependencies.Count == 0)
            {
                dependencies.Add(new PackageDependency { Ordinal = ordinal++, TargetFramework = group.TargetFramework });
                continue;
            }

            dependencies.AddRange(group.Dependencies.Select(d => new PackageDependency
            {
                Ordinal = ordinal++,
                TargetFramework = group.TargetFramework,
                Id = d.Id,
                VersionRange = d.VersionRange,
            }));
        }

        var packageTypes = "|" + string.Join('|', p.PackageTypes) + "|";
        return new PackageVersion
        {
            OriginalVersion = p.OriginalVersion,
            NormalizedVersion = normalized,
            NormalizedVersionLower = normalized.ToLowerInvariant(),
            IsPrerelease = p.Version.IsPrerelease,
            IsSemVer2 = p.IsSemVer2,
            Listed = true,
            Origin = origin,
            Authors = p.Authors,
            Description = p.Description,
            Summary = p.Summary,
            Title = p.Title,
            Tags = p.Tags,
            IconUrl = p.IconUrl,
            LicenseUrl = p.LicenseUrl,
            LicenseExpression = p.LicenseExpression,
            ProjectUrl = p.ProjectUrl,
            RepositoryUrl = p.RepositoryUrl,
            RepositoryType = p.RepositoryType,
            ReleaseNotes = p.ReleaseNotes,
            Copyright = p.Copyright,
            Language = p.Language,
            MinClientVersion = p.MinClientVersion,
            RequireLicenseAcceptance = p.RequireLicenseAcceptance,
            PackageTypes = packageTypes,
            PackageTypesLower = packageTypes.ToLowerInvariant(),
            PublishedUtc = publishedUtc,
            LastUpdatedUtc = publishedUtc,
            Size = p.Size,
            Hash = p.Sha512,
            HashAlgorithm = "SHA512",
            TagsLower = " " + string.Join(' ', tagsLower.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)) + " ",
            SearchTextLower = string.Join('\n', p.Id, p.Title, p.Tags, p.Summary, p.Description, p.Authors).ToLowerInvariant(),
            Dependencies = dependencies,
        };
    }

    /// <summary>Symbol server key of a portable PDB: 32 hex digits of the PDB id GUID plus <c>ffffffff</c>.</summary>
    private static string? TryReadPortablePdbKey(byte[] content)
    {
        // Portable PDBs start with the ECMA-335 metadata signature "BSJB"; Windows PDBs do not.
        if (content.Length < 4 || content[0] != 0x42 || content[1] != 0x53 || content[2] != 0x4A || content[3] != 0x42)
        {
            return null;
        }

        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(content));
            var header = provider.GetMetadataReader().DebugMetadataHeader;
            if (header is null || header.Id.Length < 16)
            {
                return null;
            }

            var guid = new Guid(header.Id.AsSpan()[..16]);
            return guid.ToString("N") + "ffffffff";
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
