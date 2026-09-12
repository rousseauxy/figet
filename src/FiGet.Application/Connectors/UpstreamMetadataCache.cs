using FiGet.Application.Ports;
using System.Collections.Concurrent;

namespace FiGet.Application.Connectors;

/// <summary>
/// Remembers what an upstream said about the versions of one package id. Held in memory rather than in the
/// database because it only decides what a listing reads like: a replica that has not fetched it yet shows
/// the same versions, just described once it has. Registered as a singleton.
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

    private static string Key(int upstreamKey, string idLower) => upstreamKey + "|" + idLower;
}
