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
    /// The id as the upstream spells it. Kept here because a registration URL is lower-cased by
    /// convention, so without it an uncached package is renamed to its own URL - "powershellget" until
    /// somebody downloads it and the real nuspec replaces it. Empty on rows written before this existed,
    /// which simply falls back to the requested spelling until the next refresh.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The versions the upstream reported, normalised and separated by a single space. Empty means the
    /// upstream answered but knows no such package, which is cached too so a miss is not asked twice.
    /// </summary>
    public string Versions { get; set; } = "";

    /// <summary>Versions among <see cref="Versions"/> that need SemVer 2.0.0, so a v2 client can be told less.</summary>
    public string SemVer2Versions { get; set; } = "";

    /// <summary>
    /// Versions among <see cref="Versions"/> that the upstream holds but does not advertise, separated by
    /// a single space. Persisted because the descriptions that carry this flag live in memory and do not
    /// survive a restart: without it, every version looks listed until the first refresh lands, and a
    /// version the gallery hides is briefly eligible to be "latest" again.
    /// </summary>
    public string UnlistedVersions { get; set; } = "";

    /// <summary>
    /// What each version depends on: one line per version, as <c>version</c>, a tab, then the v2
    /// convention <c>id:range:targetFramework</c> joined by pipes. A range contains spaces and commas but
    /// never a tab, colon or pipe, so this round-trips without escaping.
    ///
    /// Persisted for the same reason, and it matters more: a client reads dependencies from here to decide
    /// what else to install, so while this was empty a first install brought nothing with it. It is small
    /// - tens of kilobytes for a package with two thousand versions - unlike the descriptions and tags
    /// beside it, which are the hundred megabytes that must stay out of the database.
    /// </summary>
    public string Dependencies { get; set; } = "";

    public DateTime FetchedUtc { get; set; }

    /// <summary>
    /// True when the last attempt failed. The entry is still served while it is fresh, because answering
    /// with a slightly stale list beats failing a client's install.
    /// </summary>
    public bool Stale { get; set; }
}
