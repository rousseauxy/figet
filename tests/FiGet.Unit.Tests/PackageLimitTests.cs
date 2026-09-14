using FiGet.Domain.Packages;
using FiGet.Infrastructure.Packages;
using FiGet.Testing;

namespace FiGet.Unit.Tests;

/// <summary>What the indexer refuses to hold in memory, whatever a package declares about itself.</summary>
public sealed class PackageLimitTests
{
    private readonly PackageIndexer indexer = new();

    /// <summary>
    /// A nuspec of five megabytes of one character deflates to a few kilobytes, so the package is small and the entry is
    /// not: reading it whole was the review's S6.1. It is refused at the cap, before the description is ever built.
    /// </summary>
    [Fact]
    public async Task A_nuspec_past_the_cap_is_refused_however_small_the_package_is()
    {
        using var nupkg = TestPackages.Create("Huge.Nuspec", "1.0.0", b => b.Description = new string('x', PackageIndexer.MaxNuspecBytes + 1024));
        Assert.True(nupkg.Length < 1024 * 1024, $"The package should deflate well; it is {nupkg.Length} bytes.");

        var refused = await Assert.ThrowsAsync<InvalidPackageException>(() => indexer.IndexAsync(nupkg, CancellationToken.None));
        Assert.Contains("nuspec is larger", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_nuspec_under_the_cap_still_indexes()
    {
        using var nupkg = TestPackages.Create("Long.Description", "1.0.0", b => b.Description = new string('x', 100_000));
        var indexed = await indexer.IndexAsync(nupkg, CancellationToken.None);
        Assert.Equal(100_000, indexed.Description.Length);
    }
}
