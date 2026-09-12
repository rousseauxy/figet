using FiGet.Application.Ports;
using System.Collections.Concurrent;
using System.Linq;

namespace FiGet.Application.Connectors;

/// <summary>
/// Remembers what an upstream said about the versions of one package id. Held in memory rather than in the
/// database because it only decides what a listing reads like: a replica that has not fetched it yet shows
/// the same versions, just described once it has. Registered as a singleton.
///
/// It was briefly persisted instead, and that was a mistake worth leaving a note about. The version list
/// for PnP.PowerShell is 31 KB of normalised strings; describing those same versions is 101 MB, because a
/// PowerShell gallery encodes every exported command as a tag on every version. Holding those objects is
/// fine - this cache did it for weeks - but round-tripping them through a serialiser holds the text, the
/// buffer and the object graph at once, which is an OutOfMemoryException in a container with a gigabyte.
/// </summary>
public sealed class UpstreamMetadataCache(int maxPackages = UpstreamMetadataCache.DefaultMaxPackages)
{
    /// <summary>
    /// Ids described per replica before the oldest are dropped. Bounded since 2026-09-12: this was an
    /// unbounded dictionary that only ever evicted a key somebody happened to read while it was stale, so
    /// nothing swept it and browsing enough packages grew it without limit - one copy in every replica.
    ///
    /// Dropping an entry is safe now in a way it was not before. What a listing needs to be *correct* -
    /// which versions are hidden, what each one depends on - is in the database; what is held here is the
    /// text beside it. An evicted entry costs a listing its description until the next refresh, never its
    /// meaning.
    /// </summary>
    public const int DefaultMaxPackages = 500;

    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, IReadOnlyList<UpstreamMetadata> Items)> entries =
        new(StringComparer.Ordinal);

    private readonly int maxPackages = maxPackages > 0 ? maxPackages : DefaultMaxPackages;

    /// <summary>The remembered metadata, or null when nothing was stored or it is older than the age given.</summary>
    public IReadOnlyList<UpstreamMetadata>? Get(int upstreamKey, string idLower, DateTime nowUtc, TimeSpan maxAge)
    {
        var key = Key(upstreamKey, idLower);
        if (entries.TryGetValue(key, out var entry) && nowUtc - entry.FetchedUtc < maxAge)
        {
            return entry.Items;
        }

        entries.TryRemove(key, out _);
        return null;
    }

    public void Set(int upstreamKey, string idLower, IReadOnlyList<UpstreamMetadata> items, DateTime nowUtc)
    {
        entries[Key(upstreamKey, idLower)] = (nowUtc, items);
        Trim();
    }

    /// <summary>
    /// Drops the oldest entries once the cap is passed. Oldest *written*, not least recently read: reading
    /// does not touch the timestamp, and making it do so would mean a write on every read of a cache whose
    /// whole point is to be cheap. The heavy entries are the ones a big paged walk produced, and those age
    /// out on their own.
    ///
    /// Only when over the cap, so the usual path is one comparison.
    /// </summary>
    private void Trim()
    {
        if (entries.Count <= maxPackages)
        {
            return;
        }

        foreach (var stale in entries
            .OrderBy(e => e.Value.FetchedUtc)
            .Take(entries.Count - maxPackages)
            .Select(e => e.Key)
            .ToList())
        {
            entries.TryRemove(stale, out _);
        }
    }

    /// <summary>
    /// Forgets what was remembered about one id, which is precisely what a restart does to this cache.
    /// Exists so a test can reproduce that state without restarting anything: the version list stays in
    /// the database, the descriptions go, and what the server answers next is the thing worth asserting.
    /// </summary>
    public void Forget(int upstreamKey, string idLower) => entries.TryRemove(Key(upstreamKey, idLower), out _);

    private static string Key(int upstreamKey, string idLower) => upstreamKey + "|" + idLower;
}
