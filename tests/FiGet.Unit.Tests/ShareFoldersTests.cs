using FiGet.Application.Assets;
using FiGet.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiGet.Unit.Tests;

/// <summary>
/// Which folders the pages may offer as the content of an asset directory: the direct, real sub-folders of the shares
/// mount and nothing else. This is the whole of the rule that keeps a page from serving a folder the operator never
/// mounted for it, so the refusals matter more than the acceptances.
/// </summary>
public sealed class ShareFoldersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "figet-shares-tests-" + Guid.NewGuid().ToString("N"));

    public ShareFoldersTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "intune"));
        Directory.CreateDirectory(Path.Combine(root, "crm"));
        Directory.CreateDirectory(Path.Combine(root, ".dotted"));
        Directory.CreateDirectory(Path.Combine(root, "desktop.ini"));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "not a folder");
        if (OperatingSystem.IsWindows())
        {
            var hidden = Directory.CreateDirectory(Path.Combine(root, "hidden"));
            hidden.Attributes |= FileAttributes.Hidden;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("intune", true)]
    [InlineData("crm-2", true)]
    [InlineData("a.b", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("c:", false)]
    [InlineData(" intune", false)]
    [InlineData("intune ", false)]
    [InlineData("a\0b", false)]
    public void A_plain_name_has_no_separators_references_or_edges(string? name, bool expected)
    {
        Assert.Equal(expected, ShareFolders.IsPlainName(name));
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_not_plain()
    {
        Assert.True(ShareFolders.IsPlainName(new string('a', ShareFolders.MaxNameLength)));
        Assert.False(ShareFolders.IsPlainName(new string('a', ShareFolders.MaxNameLength + 1)));
    }

    [Fact]
    public void Lists_only_the_real_direct_sub_folders()
    {
        var shares = Create(root);
        Assert.True(shares.IsConfigured);
        var names = shares.List().Select(folder => folder.Name).ToList();
        Assert.Equal(["crm", "intune"], names);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "crm"), shares.List()[0].Path);
    }

    [Fact]
    public void Resolves_a_listed_name_to_its_path_and_nothing_else()
    {
        var shares = Create(root);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "intune"), shares.Resolve("intune"));
        foreach (var name in (string?[])[null, "", "..", ".", "notes.txt", "nothing", ".dotted", "desktop.ini", "hidden", "intune/sub", "intune\\sub", "INTUNE", "../intune", "..\\.."])
        {
            Assert.Null(shares.Resolve(name));
        }
    }

    [Fact]
    public void A_link_under_the_root_is_neither_listed_nor_resolved()
    {
        var elsewhere = Path.Combine(root, "..", "figet-shares-elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "linked"), elsewhere);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Creating a symbolic link needs a privilege this account does not have: " + ex.Message);
            }

            var shares = Create(root);
            Assert.DoesNotContain("linked", shares.List().Select(folder => folder.Name));
            Assert.Null(shares.Resolve("linked"));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_root_nothing_is_offered(string? configured)
    {
        var shares = Create(configured);
        Assert.False(shares.IsConfigured);
        Assert.Null(shares.Root);
        Assert.Empty(shares.List());
        Assert.Null(shares.Resolve("intune"));
        Assert.False(shares.IsUnderRoot(Path.Combine(root, "intune")));
    }

    [Fact]
    public void A_root_that_does_not_exist_offers_nothing_and_does_not_throw()
    {
        var shares = Create(Path.Combine(root, "missing"));
        Assert.True(shares.IsConfigured);
        Assert.Empty(shares.List());
        Assert.Null(shares.Resolve("intune"));
    }

    /// <summary>The start-up rule: configuration owns folders outside the mount, the pages own those under it.</summary>
    [Fact]
    public void Knows_which_stored_paths_lie_under_the_root()
    {
        var shares = Create(root + Path.DirectorySeparatorChar);
        Assert.True(shares.IsUnderRoot(Path.Combine(root, "intune")));
        Assert.True(shares.IsUnderRoot(Path.Combine(root, "intune", "deeper")));
        Assert.False(shares.IsUnderRoot(root));
        Assert.False(shares.IsUnderRoot(root + "-other"));
        Assert.False(shares.IsUnderRoot(Path.Combine(root, "..", "elsewhere")));
        Assert.False(shares.IsUnderRoot(null));
        Assert.False(shares.IsUnderRoot(" "));
    }

    private static ShareFolders Create(string? configured) =>
        new(new ShareFolderSettings { Root = configured }, NullLogger<ShareFolders>.Instance);
}
