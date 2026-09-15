using FiGet.Domain.Entities;
using FiGet.Domain.Packages;
using FiGet.Infrastructure.Packages;
using FiGet.Testing;
using NuGet.Packaging;

namespace FiGet.Unit.Tests;

public sealed class PackageContentTests
{
    private static readonly string[] Dependency = ["Dependency"];

    [Theory]
    [InlineData("My.Module", "My.Module.psd1|My.Module.psm1|en-US/about_My.Module.help.txt", PackageContentKind.PowerShell)]
    [InlineData("Get-Thing", "Get-Thing.ps1", PackageContentKind.PowerShell)]
    [InlineData("git", "tools/chocolateyInstall.ps1|tools/LICENSE.txt", PackageContentKind.Chocolatey)]
    [InlineData("some.module", "tools/ChocolateyUninstall.ps1|tools/some.module/some.module.psd1", PackageContentKind.Chocolatey)]
    [InlineData("Newtonsoft.Json", "lib/net6.0/Newtonsoft.Json.dll|README.md", PackageContentKind.DotNet)]
    [InlineData("Some.Analyzer", "analyzers/dotnet/cs/Some.Analyzer.dll", PackageContentKind.DotNet)]
    [InlineData("jq.portable", "tools/jq.exe", PackageContentKind.Other)]
    [InlineData("git.meta", "_rels/.rels|git.meta.nuspec|[Content_Types].xml|package/services/metadata/core-properties/x.psmdcp", PackageContentKind.MetadataOnly)]
    [InlineData("Other.Module", "Different.psd1", PackageContentKind.Other)]
    public void The_files_decide_the_kind(string id, string files, PackageContentKind expected)
    {
        Assert.Equal(expected, PackageContent.Classify(id, files.Split('|'), Dependency).Kind);
    }

    [Fact]
    public void A_dotnet_tool_is_dotnet_by_its_package_type()
    {
        var content = PackageContent.Classify("my-tool", ["tools/net8.0/any/my-tool.dll"], ["DotnetTool"]);

        Assert.Equal((PackageContentKind.DotNet, "package type DotnetTool"), (content.Kind, content.Evidence));
    }

    [Theory]
    [InlineData(FeedPurpose.Any, PackageContentKind.Chocolatey, true)]
    [InlineData(FeedPurpose.PowerShell, PackageContentKind.PowerShell, true)]
    [InlineData(FeedPurpose.PowerShell, PackageContentKind.Other, false)]
    [InlineData(FeedPurpose.PowerShell, PackageContentKind.MetadataOnly, false)]
    [InlineData(FeedPurpose.PowerShell, PackageContentKind.DotNet, false)]
    [InlineData(FeedPurpose.NuGet, PackageContentKind.DotNet, true)]
    [InlineData(FeedPurpose.NuGet, PackageContentKind.Other, true)]
    [InlineData(FeedPurpose.NuGet, PackageContentKind.PowerShell, false)]
    [InlineData(FeedPurpose.NuGet, PackageContentKind.Chocolatey, false)]
    [InlineData(FeedPurpose.Chocolatey, PackageContentKind.Chocolatey, true)]
    [InlineData(FeedPurpose.Chocolatey, PackageContentKind.Other, true)]
    [InlineData(FeedPurpose.Chocolatey, PackageContentKind.MetadataOnly, true)]
    [InlineData(FeedPurpose.Chocolatey, PackageContentKind.PowerShell, false)]
    [InlineData(FeedPurpose.Chocolatey, PackageContentKind.DotNet, false)]
    public void Each_purpose_takes_its_own_kind(FeedPurpose purpose, PackageContentKind kind, bool accepted)
    {
        Assert.Equal(accepted, new PackageContent(kind, "x").RefusalFor(purpose, "Some.Id", "feed") is null);
    }

    [Fact]
    public void A_refusal_names_what_was_found_and_what_the_feed_is_for()
    {
        var refusal = new PackageContent(PackageContentKind.Chocolatey, "tools/chocolateyInstall.ps1").RefusalFor(FeedPurpose.PowerShell, "git", "modules");

        Assert.Equal("git is a Chocolatey package (tools/chocolateyInstall.ps1), and feed 'modules' is for PowerShell modules. Push it to another feed, or set this feed's \"Used for\" to any client.", refusal);
    }

    /// <summary>The indexer passes the archive's own file names, packaging files and all.</summary>
    [Fact]
    public async Task The_indexer_classifies_a_real_archive()
    {
        using var module = TestPackages.Create("Real.Module", "1.0.0", b => b.AddContent("Real.Module.psd1", "@{ ModuleVersion = '1.0.0' }"u8.ToArray()));
        using var choco = TestPackages.Create("real.choco", "1.0.0", b =>
        {
            b.Files.Clear();
            b.AddContent("tools/chocolateyinstall.ps1", "Write-Host hi"u8.ToArray());
        });
        using var meta = TestPackages.Create("real.meta", "1.0.0", b =>
        {
            b.Files.Clear();
            b.AddDependency("any", "real.choco", "1.0.0");
        });

        var indexer = new PackageIndexer();
        Assert.Equal(PackageContentKind.PowerShell, (await indexer.IndexAsync(module, CancellationToken.None)).Content.Kind);
        Assert.Equal(PackageContentKind.Chocolatey, (await indexer.IndexAsync(choco, CancellationToken.None)).Content.Kind);
        Assert.Equal(PackageContentKind.MetadataOnly, (await indexer.IndexAsync(meta, CancellationToken.None)).Content.Kind);
        using var library = TestPackages.Create("Real.Library", "1.0.0");
        Assert.Equal(PackageContentKind.DotNet, (await indexer.IndexAsync(library, CancellationToken.None)).Content.Kind);
    }
}
