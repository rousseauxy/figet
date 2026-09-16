using FiGet.Domain.Versions;
using NuGet.Versioning;

namespace FiGet.Unit.Tests;

/// <summary>The rule a change report marks rows with, and the one it uses to say what came before.</summary>
public sealed class VersionChangeTests
{
    [Theory]
    [InlineData("1.9.4", "2.0.0", true)]
    [InlineData("1.9.4", "1.10.0", false)]
    [InlineData("2.0.0", "2.0.1", false)]
    [InlineData("1.2.3", "2.0.0-beta1", true)]
    [InlineData("2.0.0-beta1", "2.0.0", false)]
    [InlineData("1.0.0", "3.0.0", true)]
    public void A_higher_major_is_the_breaking_one(string previous, string next, bool breaking)
    {
        Assert.Equal(breaking, VersionChange.IsBreaking(NuGetVersion.Parse(previous), NuGetVersion.Parse(next)));
    }

    /// <summary>Nothing came before, so nothing can have broken.</summary>
    [Fact]
    public void A_first_version_is_not_breaking()
    {
        Assert.False(VersionChange.IsBreaking(null, NuGetVersion.Parse("3.0.0")));
    }

    [Fact]
    public void The_previous_version_is_the_highest_one_below()
    {
        var held = Versions("1.0.0", "1.9.4", "2.0.0", "3.0.0");

        Assert.Equal("1.9.4", VersionChange.Previous(held, NuGetVersion.Parse("2.0.0"))?.ToNormalizedString());
        Assert.Equal("3.0.0", VersionChange.Previous(held, NuGetVersion.Parse("4.0.0"))?.ToNormalizedString());
        Assert.Null(VersionChange.Previous(held, NuGetVersion.Parse("1.0.0")));
        Assert.Null(VersionChange.Previous([], NuGetVersion.Parse("1.0.0")));
    }

    /// <summary>
    /// Compared as NuGet compares versions, which is the whole reason this lives beside the version list builder: a
    /// string sort puts 1.9.0 above 1.10.0 and would report the wrong previous version for every tenth release.
    /// </summary>
    [Fact]
    public void Versions_are_compared_as_nuget_does_them()
    {
        Assert.Equal("1.9.0", VersionChange.Previous(Versions("1.9.0", "1.10.0"), NuGetVersion.Parse("1.10.0"))?.ToNormalizedString());
        Assert.Null(VersionChange.Previous(Versions("1.0", "1.0.0.0"), NuGetVersion.Parse("1.0.0")));
        Assert.Equal("1.0.0", VersionChange.Previous(Versions("1.0", "2.0"), NuGetVersion.Parse("1.1"))?.ToNormalizedString());
    }

    /// <summary>A prerelease sits below its own release, so the release's previous version is the prerelease.</summary>
    [Fact]
    public void A_prerelease_comes_before_its_release()
    {
        var held = Versions("1.0.0", "2.0.0-beta1");

        Assert.Equal("2.0.0-beta1", VersionChange.Previous(held, NuGetVersion.Parse("2.0.0"))?.ToNormalizedString());
        Assert.Equal("1.0.0", VersionChange.Previous(held, NuGetVersion.Parse("2.0.0-beta1"))?.ToNormalizedString());
    }

    private static NuGetVersion[] Versions(params string[] versions) => [.. versions.Select(NuGetVersion.Parse)];
}
