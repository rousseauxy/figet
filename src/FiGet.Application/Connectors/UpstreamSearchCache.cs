using System.Collections.Concurrent;
using FiGet.Application.Ports;

namespace FiGet.Application.Connectors;

/// <summary>
/// What one upstream answered to one search, held for a few minutes, and the search itself while it is still running.
/// Registered as a singleton, so a search that outlives the request that started it still lands here for the next one.
///
/// Why it exists (FG-64, measured on a test cluster on 2026-11-27): every term search on a proxy feed asked the public
/// gallery again, synchronously. The local queries took 4-18 ms each; the gallery added 1.3-2 s, and 5 s under load.
/// Typing in a search box or running Find-Module twice asked the same question each time.
///
/// A failed search is not kept: the next request asks again. A running one is shared, so twenty clients searching for
/// the same term while the gallery is slow cause one upstream search, not twenty.
/// </summary>
public sealed class UpstreamSearchCache(TimeProvider time, int maxEntries = UpstreamSearchCache.DefaultMaxEntries)
{
    /// <summary>
    /// Searches held per replica before the oldest are dropped. One entry is at most a few hundred hits of id, version,
    /// description and tags; a PowerShell gallery's tags can run to kilobytes per hit, so this stays modest.
    /// </summary>
    public const int DefaultMaxEntries = 1000;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    private readonly int maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;

    /// <summary>
    /// The hits for <paramref name="key"/>: a kept answer younger than <paramref name="ttl"/>, the search already running
    /// for it, or a new one started with <paramref name="search"/>. The search returns null when the upstream did not
    /// answer, and that is dropped rather than kept. The returned task never faults for an upstream failure.
    /// </summary>
    public Task<IReadOnlyList<UpstreamSearchHit>?> GetOrStart(string key, TimeSpan ttl, Func<Task<IReadOnlyList<UpstreamSearchHit>?>> search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var now = time.GetUtcNow().UtcDateTime;
        while (true)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                if (existing.IsUsable(now, ttl))
                {
                    return existing.Hits;
                }

                // Expired or failed: replace it, unless another request replaced it first.
                var replacement = new Entry(now);
                if (entries.TryUpdate(key, replacement, existing))
                {
                    return replacement.Start(this, key, search);
                }

                continue;
            }

            var entry = new Entry(now);
            if (entries.TryAdd(key, entry))
            {
                Trim(now, ttl);
                return entry.Start(this, key, search);
            }
        }
    }

    /// <summary>How many searches are held, finished or running. For tests and the system page.</summary>
    public int Count => entries.Count;

    private void Completed(string key, Entry entry, IReadOnlyList<UpstreamSearchHit>? hits)
    {
        if (hits is null)
        {
            // Only this entry: a newer one for the same key may already be running.
            entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            return;
        }

        entry.CompletedUtc = time.GetUtcNow().UtcDateTime;
    }

    private void Trim(DateTime now, TimeSpan ttl)
    {
        if (entries.Count <= maxEntries)
        {
            return;
        }

        foreach (var (key, entry) in entries)
        {
            if (entry.CompletedUtc is { } completed && now - completed >= ttl)
            {
                entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }

        var excess = entries.Count - maxEntries;
        if (excess <= 0)
        {
            return;
        }

        // Still over: the oldest finished ones go first. A running search is never dropped, so a request waiting on it
        // and the one that follows still meet the same answer.
        foreach (var (key, entry) in entries.Where(e => e.Value.CompletedUtc is not null).OrderBy(e => e.Value.StartedUtc).Take(excess).ToList())
        {
            entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
    }

    private sealed class Entry(DateTime startedUtc)
    {
        // Created with the entry, so a request that finds the entry before its search has started waits on it too.
        private readonly TaskCompletionSource<IReadOnlyList<UpstreamSearchHit>?> answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTime StartedUtc { get; } = startedUtc;

        /// <summary>When the upstream answered; null while the search runs.</summary>
        public DateTime? CompletedUtc { get; set; }

        public Task<IReadOnlyList<UpstreamSearchHit>?> Hits => answer.Task;

        public bool IsUsable(DateTime now, TimeSpan ttl) =>
            !answer.Task.IsCompleted || (CompletedUtc is { } completed && now - completed < ttl);

        public Task<IReadOnlyList<UpstreamSearchHit>?> Start(UpstreamSearchCache cache, string key, Func<Task<IReadOnlyList<UpstreamSearchHit>?>> search)
        {
            _ = RunAsync(cache, key, search).ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return answer.Task;
        }

        private async Task RunAsync(UpstreamSearchCache cache, string key, Func<Task<IReadOnlyList<UpstreamSearchHit>?>> search)
        {
            IReadOnlyList<UpstreamSearchHit>? found = null;
            try
            {
                // Off the caller's thread at once, so the request can stop waiting on its own clock.
                await Task.Yield();
                found = await search();
            }
            finally
            {
                // The search reports its own failures; whatever escapes it is still not kept.
                cache.Completed(key, this, found);
                answer.TrySetResult(found);
            }
        }
    }
}
