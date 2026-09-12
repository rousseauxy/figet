using FiGet.Application.Ports;
using System.Collections.Concurrent;

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
public sealed class UpstreamMetadataCache
{
    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, IReadOnlyList<UpstreamMetadata> Items)> entries =
        new(StringComparer.Ordinal);

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

    public void Set(int upstreamKey, string idLower, IReadOnlyList<UpstreamMetadata> items, DateTime nowUtc) =>
        entries[Key(upstreamKey, idLower)] = (nowUtc, items);

    /// <summary>
    /// Forgets what was remembered about one id, which is precisely what a restart does to this cache.
    /// Exists so a test can reproduce that state without restarting anything: the version list stays in
    /// the database, the descriptions go, and what the server answers next is the thing worth asserting.
    /// </summary>
    public void Forget(int upstreamKey, string idLower) => entries.TryRemove(Key(upstreamKey, idLower), out _);

    private static string Key(int upstreamKey, string idLower) => upstreamKey + "|" + idLower;
}
