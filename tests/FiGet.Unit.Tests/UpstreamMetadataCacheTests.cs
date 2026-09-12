using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using NuGet.Versioning;

namespace FiGet.Unit.Tests;

/// <summary>
/// The bound on what one replica keeps described in memory.
///
/// This was an unbounded dictionary. It only ever dropped a key somebody happened to read while it was
/// stale, so nothing swept it, and browsing enough packages grew it without limit - in every replica at
/// once, which is what makes it a deployment problem rather than a tidiness one. Describing a couple of
/// thousand versions of one module is around a hundred megabytes.
///
/// Evicting is safe here in a way it was not before: what a listing needs to be *correct* - which versions
/// are hidden, what each depends on - is in the database now, and what this holds is the text beside it.
/// </summary>
public sealed class UpstreamMetadataCacheTests
{
    [Fact]
    public void The_oldest_entries_go_when_the_cap_is_passed()
    {
        var cache = new UpstreamMetadataCache(maxPackages: 3);
        var start = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

        // Written oldest first, so "oldest" and "first" are the same thing here.
        for (var i = 0; i < 5; i++)
        {
            cache.Set(1, "package" + i, Described(), start.AddMinutes(i));
        }

        var asked = start.AddHours(1);
        Assert.Null(cache.Get(1, "package0", asked, TimeSpan.MaxValue));
        Assert.Null(cache.Get(1, "package1", asked, TimeSpan.MaxValue));

        // The three most recently written survive, which is the whole contract.
        Assert.NotNull(cache.Get(1, "package2", asked, TimeSpan.MaxValue));
        Assert.NotNull(cache.Get(1, "package3", asked, TimeSpan.MaxValue));
        Assert.NotNull(cache.Get(1, "package4", asked, TimeSpan.MaxValue));
    }

    /// <summary>
    /// Under the cap nothing is dropped. Asserted because the damaging failure is not a cache that grows -
    /// that is the defect being fixed - but one that trims too eagerly and quietly stops being a cache.
    /// </summary>
    [Fact]
    public void Nothing_is_dropped_below_the_cap()
    {
        var cache = new UpstreamMetadataCache(maxPackages: 10);
        var start = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 10; i++)
        {
            cache.Set(1, "package" + i, Described(), start.AddMinutes(i));
        }

        var asked = start.AddHours(1);
        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(cache.Get(1, "package" + i, asked, TimeSpan.MaxValue));
        }
    }

    /// <summary>The same id under two upstreams is two entries, since either may describe it differently.</summary>
    [Fact]
    public void Two_upstreams_describing_one_id_are_counted_separately()
    {
        var cache = new UpstreamMetadataCache(maxPackages: 2);
        var start = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

        cache.Set(1, "shared", Described(), start);
        cache.Set(2, "shared", Described(), start.AddMinutes(1));

        var asked = start.AddHours(1);
        Assert.NotNull(cache.Get(1, "shared", asked, TimeSpan.MaxValue));
        Assert.NotNull(cache.Get(2, "shared", asked, TimeSpan.MaxValue));
    }

    private static IReadOnlyList<UpstreamMetadata> Described() =>
    [
        new UpstreamMetadata(
            NuGetVersion.Parse("1.0.0"),
            "A described version.",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            null,
            0),
    ];
}
