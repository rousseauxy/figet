using FiGet.Application.Packages;
using FiGet.Domain.Entities;

namespace FiGet.Unit.Tests;

public sealed class RetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Keeps_the_newest_stable_versions_and_removes_the_rest()
    {
        var removed = Plan(new RetentionRules(KeepStable: 2), Pushed("1.0.0"), Pushed("1.1.0"), Pushed("1.2.0"), Pushed("2.0.0"));

        Assert.Equal(["1.0.0", "1.1.0"], Versions(removed));
        Assert.All(removed, r => Assert.Equal(RetentionReason.OlderStable, r.Reason));
    }

    [Fact]
    public void Versions_are_ordered_as_versions_not_as_text()
    {
        var removed = Plan(new RetentionRules(KeepStable: 1), Pushed("1.9.0"), Pushed("1.10.0"));

        Assert.Equal(["1.9.0"], Versions(removed));
    }

    [Fact]
    public void The_newest_version_of_a_package_is_kept_whatever_the_counts()
    {
        var removed = Plan(new RetentionRules(KeepPrerelease: 0), Pushed("1.0.0"), Pushed("2.0.0-beta.1"), Pushed("1.5.0-beta.1"));

        Assert.Equal(["1.5.0-beta.1"], Versions(removed));
    }

    [Fact]
    public void Counting_per_major_version_keeps_each_line()
    {
        var removed = Plan(new RetentionRules(KeepStable: 1, PerMajorVersion: true), Pushed("1.0.0"), Pushed("1.1.0"), Pushed("2.0.0"), Pushed("2.1.0"));

        Assert.Equal(["1.0.0", "2.0.0"], Versions(removed));
    }

    [Fact]
    public void A_recently_downloaded_version_is_kept()
    {
        var removed = Plan(new RetentionRules(KeepStable: 1, KeepIfUsedWithinDays: 30), Pushed("1.0.0", usedDaysAgo: 3), Pushed("1.1.0", usedDaysAgo: 90), Pushed("1.2.0"));

        Assert.Equal(["1.1.0"], Versions(removed));
    }

    [Fact]
    public void When_the_feed_unlists_only_listed_versions_count()
    {
        var versions = new[] { Pushed("1.0.0"), Pushed("1.1.0", listed: false), Pushed("1.2.0") };

        Assert.Equal(["1.0.0"], Versions(Plan(new RetentionRules(KeepStable: 1), versions)));
        Assert.Equal(["1.0.0", "1.1.0"], Versions(RetentionPolicy.Plan(versions, new RetentionRules(KeepStable: 1), PackageDeletionBehavior.HardDelete, Now)));
    }

    [Fact]
    public void Cached_copies_are_pruned_by_last_use_and_pushed_versions_are_not()
    {
        var removed = Plan(
            new RetentionRules(PruneCachedAfterDays: 30),
            Cached("1.0.0", usedDaysAgo: 40),
            Cached("1.1.0", usedDaysAgo: 10),
            Pushed("0.9.0", usedDaysAgo: 400));

        var only = Assert.Single(removed);
        Assert.Equal("1.0.0", only.Version.NormalizedVersion);
        Assert.Equal(RetentionReason.UnusedCache, only.Reason);
    }

    [Fact]
    public void Retention_counts_do_not_touch_cached_copies()
    {
        var removed = Plan(new RetentionRules(KeepStable: 1), Cached("1.0.0"), Cached("1.1.0"), Pushed("2.0.0"));

        Assert.Empty(removed);
    }

    [Theory]
    [InlineData(0, null, null, null, false)]
    [InlineData(1, -1, null, null, false)]
    [InlineData(null, null, 0, null, false)]
    [InlineData(null, null, null, 0, false)]
    [InlineData(1, 0, 1, 1, true)]
    public void Settings_are_checked(int? stable, int? prerelease, int? usedDays, int? pruneDays, bool valid) =>
        Assert.Equal(valid, new RetentionRules(stable, prerelease, false, usedDays, pruneDays).Problem() is null);

    private static IReadOnlyList<RetentionRemoval> Plan(RetentionRules rules, params RetentionCandidate[] versions) =>
        RetentionPolicy.Plan(versions, rules, PackageDeletionBehavior.Unlist, Now);

    private static string[] Versions(IReadOnlyList<RetentionRemoval> removals) =>
        removals.Select(r => r.Version.NormalizedVersion).Order(StringComparer.Ordinal).ToArray();

    private static RetentionCandidate Pushed(string version, bool listed = true, int usedDaysAgo = 365) =>
        new("Pkg", "pkg", version, version.Contains('-', StringComparison.Ordinal), listed, PackageOrigin.Pushed, Now.AddDays(-usedDaysAgo), 1000);

    private static RetentionCandidate Cached(string version, int usedDaysAgo = 365) =>
        new("Pkg", "pkg", version, version.Contains('-', StringComparison.Ordinal), true, PackageOrigin.Cached, Now.AddDays(-usedDaysAgo), 1000);
}
