namespace FiGet.Domain.Entities;

/// <summary>
/// What an upstream said about one version of one package: the text a listing shows beside it. Stored so a restart
/// does not leave every proxied package reading plainly until it is refreshed.
///
/// One row per version rather than one per package, and the tags kept apart in <see cref="CachedUpstreamTagSet"/>.
/// The single-blob shape was tried and taken out: a PowerShell gallery writes every exported command as a tag on
/// every version, so PnP.PowerShell described is 101 MB, and round-tripping that as one serialised value ran the
/// container out of memory. Rows are written in small batches and only for versions not stored yet, and consecutive
/// versions of a module mostly share their tag list, which is stored once.
///
/// Whether a version is listed and what it depends on are not here: those change what a client is told, and live on
/// <see cref="CachedUpstreamIndex"/> beside the version list.
/// </summary>
public sealed class CachedUpstreamDescription
{
    public long Key { get; set; }

    public int FeedUpstreamKey { get; set; }

    public FeedUpstream? FeedUpstream { get; set; }

    public required string IdLower { get; set; }

    public required string NormalizedVersion { get; set; }

    public string Title { get; set; } = "";

    public string Summary { get; set; } = "";

    public string Description { get; set; } = "";

    public string Authors { get; set; } = "";

    public string ProjectUrl { get; set; } = "";

    public string IconUrl { get; set; } = "";

    public string LicenseUrl { get; set; } = "";

    public DateTime? PublishedUtc { get; set; }

    /// <summary>As of when the row was written; the in-memory description a refresh brings is newer.</summary>
    public long Downloads { get; set; }

    /// <summary>The <see cref="CachedUpstreamTagSet.Hash"/> of this version's tags.</summary>
    public required string TagSetHash { get; set; }
}

/// <summary>One distinct tag list, stored once however many versions carry it. Keyed by the SHA-256 of the text.</summary>
public sealed class CachedUpstreamTagSet
{
    public required string Hash { get; set; }

    public string Tags { get; set; } = "";
}
