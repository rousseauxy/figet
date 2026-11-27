using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using NuGet.Versioning;

namespace FiGet.Unit.Tests;

/// <summary>
/// The kept upstream search answers (FG-64): reused within the TTL, shared while running, never kept when the upstream
/// failed, and bounded per replica.
/// </summary>
public sealed class UpstreamSearchCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task An_answer_is_reused_within_its_ttl_and_asked_again_after()
    {
        var clock = new ManualClock();
        var cache = new UpstreamSearchCache(clock);
        var asked = 0;
        Task<IReadOnlyList<UpstreamSearchHit>?> Search()
        {
            asked++;
            return Task.FromResult<IReadOnlyList<UpstreamSearchHit>?>([Hit("Found")]);
        }

        Assert.Equal("Found", Assert.Single((await cache.GetOrStart("k", Ttl, Search))!).Id);
        clock.Advance(Ttl - TimeSpan.FromSeconds(1));
        await cache.GetOrStart("k", Ttl, Search);
        Assert.Equal(1, asked);

        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetOrStart("k", Ttl, Search);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task A_running_search_is_shared_and_a_failed_one_is_not_kept()
    {
        var cache = new UpstreamSearchCache(new ManualClock());
        var gate = new TaskCompletionSource<IReadOnlyList<UpstreamSearchHit>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        Task<IReadOnlyList<UpstreamSearchHit>?> Slow()
        {
            asked++;
            return gate.Task;
        }

        var first = cache.GetOrStart("k", Ttl, Slow);
        var second = cache.GetOrStart("k", Ttl, Slow);
        Assert.False(first.IsCompleted);

        // The upstream did not answer: both waiters see that, and nothing is kept.
        gate.SetResult(null);
        Assert.Null(await first);
        Assert.Null(await second);
        Assert.Equal(1, asked);

        var again = await cache.GetOrStart("k", Ttl, () => Task.FromResult<IReadOnlyList<UpstreamSearchHit>?>([Hit("Later")]));
        Assert.Equal("Later", Assert.Single(again!).Id);
    }

    [Fact]
    public async Task A_search_that_throws_is_not_kept()
    {
        var cache = new UpstreamSearchCache(new ManualClock());
        Assert.Null(await cache.GetOrStart("k", Ttl, () => throw new InvalidOperationException("boom")));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task The_oldest_answers_go_past_the_bound()
    {
        var clock = new ManualClock();
        var cache = new UpstreamSearchCache(clock, maxEntries: 3);
        for (var i = 0; i < 10; i++)
        {
            await cache.GetOrStart("k" + i, Ttl, () => Task.FromResult<IReadOnlyList<UpstreamSearchHit>?>([Hit("P")]));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(cache.Count <= 3, $"{cache.Count} entries kept against a bound of 3.");

        // The newest is still there.
        var asked = false;
        await cache.GetOrStart("k9", Ttl, () =>
        {
            asked = true;
            return Task.FromResult<IReadOnlyList<UpstreamSearchHit>?>([]);
        });
        Assert.False(asked);
    }

    private static UpstreamSearchHit Hit(string id) => new(id, NuGetVersion.Parse("1.0.0"), "", "", "", 0);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
