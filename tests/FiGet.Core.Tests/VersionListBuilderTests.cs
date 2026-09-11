using FiGet.Core.Versions;
using NuGet.Versioning;

namespace FiGet.Core.Tests;

public sealed class VersionListBuilderTests
{
    private static VersionCandidate<string> Local(string version, bool listed = true, bool semVer2 = false) =>
        new(NuGetVersion.Parse(version), listed, semVer2, VersionSource.Local, "local:" + version);

    private static VersionCandidate<string> Upstream(string version, bool listed = true) =>
        new(NuGetVersion.Parse(version), listed, false, VersionSource.Upstream, "upstream:" + version);

    [Fact]
    public void Latest_flags_are_on_exactly_one_entry_each()
    {
        var list = VersionListBuilder.Build([Local("1.0.0"), Local("2.0.0-beta"), Local("1.5.0")], includeSemVer2: true);

        Assert.Equal(["1.0.0", "1.5.0", "2.0.0-beta"], list.Select(e => e.Version.ToNormalizedString()));
        Assert.Single(list, e => e.IsLatestVersion);
        Assert.Single(list, e => e.IsAbsoluteLatestVersion);
        Assert.True(list.Single(e => e.Version.ToNormalizedString() == "1.5.0").IsLatestVersion);
        Assert.True(list.Single(e => e.Version.ToNormalizedString() == "2.0.0-beta").IsAbsoluteLatestVersion);
    }

    [Fact]
    public void A_cached_version_and_a_newer_upstream_version_merge_into_one_list_with_one_latest()
    {
        // The failure this rule exists for: v1 cached locally, v2 released upstream. Two "latest" entries
        // for one id break Find-Module and Update-Module.
        var list = VersionListBuilder.Build([Local("1.0.0"), Upstream("1.0.0"), Upstream("2.0.0")], includeSemVer2: true);

        Assert.Equal(2, list.Count);
        var latest = Assert.Single(list, e => e.IsLatestVersion);
        Assert.Equal("2.0.0", latest.Version.ToNormalizedString());
        Assert.Single(list, e => e.IsAbsoluteLatestVersion);
    }

    [Fact]
    public void Local_wins_over_upstream_for_the_same_version()
    {
        var list = VersionListBuilder.Build([Upstream("1.0.0"), Local("1.0.0")], includeSemVer2: true);

        var entry = Assert.Single(list);
        Assert.Equal(VersionSource.Local, entry.Source);
        Assert.Equal("local:1.0.0", entry.Payload);
    }

    [Fact]
    public void Versions_that_differ_only_in_format_are_the_same_version()
    {
        var list = VersionListBuilder.Build([Local("1.0.0"), Upstream("1.0"), Upstream("1.0.0.0")], includeSemVer2: true);

        Assert.Single(list);
    }

    [Fact]
    public void Unlisted_versions_are_listed_but_never_latest()
    {
        var list = VersionListBuilder.Build([Local("1.0.0"), Local("2.0.0", listed: false)], includeSemVer2: true);

        Assert.Equal(2, list.Count);
        Assert.Equal("1.0.0", Assert.Single(list, e => e.IsLatestVersion).Version.ToNormalizedString());
        Assert.Equal("1.0.0", Assert.Single(list, e => e.IsAbsoluteLatestVersion).Version.ToNormalizedString());
    }

    [Fact]
    public void SemVer2_versions_are_removed_before_latest_is_computed_for_older_clients()
    {
        var candidates = new[] { Local("1.0.0"), Local("2.0.0", semVer2: true) };

        var semVer1 = VersionListBuilder.Build(candidates, includeSemVer2: false);
        var semVer2 = VersionListBuilder.Build(candidates, includeSemVer2: true);

        Assert.Equal("1.0.0", Assert.Single(semVer1).Version.ToNormalizedString());
        Assert.True(semVer1[0].IsLatestVersion);
        Assert.Equal("2.0.0", Assert.Single(semVer2, e => e.IsLatestVersion).Version.ToNormalizedString());
    }

    [Fact]
    public void Ordering_is_NuGet_version_order_including_four_part_versions()
    {
        var list = VersionListBuilder.Build([Local("10.0.0"), Local("9.0.0"), Local("1.2.3.4"), Local("1.2.3"), Local("1.2.3-alpha")], includeSemVer2: true);

        Assert.Equal(["1.2.3-alpha", "1.2.3", "1.2.3.4", "9.0.0", "10.0.0"], list.Select(e => e.Version.ToNormalizedString()));
    }

    [Fact]
    public void A_list_with_only_prereleases_has_no_latest_stable()
    {
        var list = VersionListBuilder.Build([Local("1.0.0-beta"), Local("1.0.0-rc")], includeSemVer2: true);

        Assert.DoesNotContain(list, e => e.IsLatestVersion);
        Assert.Null(list.Latest(includePrerelease: false));
        Assert.Equal("1.0.0-rc", list.Latest(includePrerelease: true)!.Version.ToNormalizedString());
    }

    [Fact]
    public void An_empty_list_is_empty()
    {
        Assert.Empty(VersionListBuilder.Build(Array.Empty<VersionCandidate<string>>(), includeSemVer2: true));
    }
}
