namespace FiGet.Domain.Entities;

/// <summary>
/// The version list one upstream reported for one package id, cached for a short time (build plan
/// section 5, rule 3). It lives in the database rather than in memory so every replica answers the same
/// thing, and so a new gallery release becomes visible everywhere within one time-to-live.
/// </summary>
public sealed class CachedUpstreamIndex
{
    public long Key { get; set; }

    public int FeedUpstreamKey { get; set; }

    public FeedUpstream? FeedUpstream { get; set; }

    /// <summary>Lower-cased package id: the lookup key.</summary>
    public required string IdLower { get; set; }

    /// <summary>
    /// The versions the upstream reported, normalised and separated by a single space. Empty means the
    /// upstream answered but knows no such package, which is cached too so a miss is not asked twice.
    /// </summary>
    public string Versions { get; set; } = "";

    /// <summary>Versions among <see cref="Versions"/> that need SemVer 2.0.0, so a v2 client can be told less.</summary>
    public string SemVer2Versions { get; set; } = "";

    /// <summary>
    /// What the upstream said about each of those versions, encoded by the adapter that wrote it.
    /// Stored rather than held in memory because otherwise every restart and every new replica pays the
    /// full walk again on the first view of a package - which for a two-thousand-version package is the
    /// difference between a page and a wait.
    /// </summary>
    public string Metadata { get; set; } = "";

    public DateTime FetchedUtc { get; set; }

    /// <summary>
    /// True when the last attempt failed. The entry is still served while it is fresh, because answering
    /// with a slightly stale list beats failing a client's install.
    /// </summary>
    public bool Stale { get; set; }
}
