using System.IO.Compression;
using System.Security.Cryptography;
using FiGet.Core.Packages;
using FiGet.Testing;
using NuGet.Packaging;

namespace FiGet.Core.Tests;

public sealed class PackageIndexerTests
{
    private readonly PackageIndexer indexer = new();

    [Fact]
    public async Task Reads_metadata_hash_and_size()
    {
        using var nupkg = TestPackages.Create("My.Module", "1.2.3", b =>
        {
            b.Title = "My Module";
            b.Summary = "Short";
            b.Tags.Add("PSModule");
            b.Tags.Add("PSFunction_Get-Thing");
            b.ProjectUrl = new Uri("https://example.org/project");
            b.Copyright = "(c) Tests";
            b.RequireLicenseAcceptance = false;
        });
        var expectedHash = Convert.ToBase64String(SHA512.HashData(nupkg.ToArray()));

        var indexed = await indexer.IndexAsync(nupkg, CancellationToken.None);

        Assert.Equal("My.Module", indexed.Id);
        Assert.Equal("1.2.3", indexed.Version.ToNormalizedString());
        Assert.False(indexed.IsSemVer2);
        Assert.Equal("My Module", indexed.Title);
        Assert.Equal("PSModule PSFunction_Get-Thing", indexed.Tags);
        Assert.Equal("https://example.org/project", indexed.ProjectUrl);
        Assert.Equal(["Dependency"], indexed.PackageTypes);
        Assert.Equal(nupkg.Length, indexed.Size);
        Assert.Equal(expectedHash, indexed.Sha512);
        Assert.NotEmpty(indexed.Nuspec);
        Assert.Equal(0, nupkg.Position);
    }

    [Fact]
    public async Task Keeps_dependency_groups_including_empty_ones()
    {
        using var nupkg = TestPackages.Create("With.Deps", "1.0.0", b =>
        {
            b.AddDependency("netstandard2.0", "Newtonsoft.Json", "[13.0.1, )");
            b.AddDependency("netstandard2.0", "Other", "[1.0.0, 2.0.0)");
            b.DependencyGroups.Add(new PackageDependencyGroup(NuGet.Frameworks.NuGetFramework.Parse("net8.0"), []));
        });

        var indexed = await indexer.IndexAsync(nupkg, CancellationToken.None);

        Assert.Equal(2, indexed.DependencyGroups.Count);
        var standard = Assert.Single(indexed.DependencyGroups, g => g.TargetFramework == "netstandard2.0");
        Assert.Equal([new IndexedDependency("Newtonsoft.Json", "[13.0.1, )"), new IndexedDependency("Other", "[1.0.0, 2.0.0)")], standard.Dependencies);
        Assert.Empty(Assert.Single(indexed.DependencyGroups, g => g.TargetFramework == "net8.0").Dependencies);
    }

    [Fact]
    public async Task A_semver2_dependency_makes_the_package_semver2()
    {
        using var nupkg = TestPackages.Create("Needs.SemVer2", "1.0.0", b => b.AddDependency("netstandard2.0", "Dep", "[1.0.0-beta.1, )"));

        Assert.True((await indexer.IndexAsync(nupkg, CancellationToken.None)).IsSemVer2);
    }

    [Theory]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0+build.5")]
    public async Task SemVer2_versions_are_detected(string version)
    {
        using var nupkg = TestPackages.Create("SemVer2.Package", version);

        Assert.True((await indexer.IndexAsync(nupkg, CancellationToken.None)).IsSemVer2);
    }

    [Fact]
    public async Task Four_part_versions_normalise_without_losing_the_fourth_part()
    {
        using var zeroRevision = TestPackages.Create("Four.Part", "1.2.3.0");
        using var revision = TestPackages.Create("Four.Part", "1.2.3.4");

        Assert.Equal("1.2.3", (await indexer.IndexAsync(zeroRevision, CancellationToken.None)).Version.ToNormalizedString());
        Assert.Equal("1.2.3.4", (await indexer.IndexAsync(revision, CancellationToken.None)).Version.ToNormalizedString());
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_rejected()
    {
        using var garbage = new MemoryStream("this is not a package"u8.ToArray());

        await Assert.ThrowsAsync<InvalidPackageException>(() => indexer.IndexAsync(garbage, CancellationToken.None));
    }

    [Fact]
    public async Task A_zip_without_a_nuspec_is_rejected()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = await archive.CreateEntry("lib/readme.txt").OpenAsync();
            await entry.WriteAsync("hello"u8.ToArray());
        }

        zip.Position = 0;
        var ex = await Assert.ThrowsAsync<InvalidPackageException>(() => indexer.IndexAsync(zip, CancellationToken.None));
        Assert.Contains("nuspec", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Symbol_packages_are_identified_by_package_type()
    {
        using var snupkg = TestPackages.CreateSymbols("Has.Symbols", "1.0.0", TestPackages.PortablePdb(), "Has.Symbols.pdb");

        Assert.Contains("SymbolsPackage", (await indexer.IndexAsync(snupkg, CancellationToken.None)).PackageTypes);
    }
}
