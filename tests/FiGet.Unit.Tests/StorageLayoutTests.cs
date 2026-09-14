using FiGet.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiGet.Unit.Tests;

/// <summary>
/// The move from folders named by feed name to folders named by feed key. It runs against the files of a live server on
/// its first start after the upgrade, so what it must never do is lose or overwrite a file, whatever state it finds.
/// </summary>
public sealed class StorageLayoutTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("figet-layout").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Every_area_of_a_feed_moves_into_the_folder_of_its_key()
    {
        Write("packages/modules/pkg/1.0.0/pkg.1.0.0.nupkg", "nupkg");
        Write("symbols/modules/lib.pdb/abc/lib.pdb", "pdb");
        Write("assets/repo/ab/abcdef", "blob");
        Write("asset-uploads/repo/0123/manifest", "1 1");

        var moved = StorageLayout.MoveNameFoldersToKeyFolders(root, [(3, "modules"), (12, "repo")], NullLogger.Instance);

        Assert.Equal(4, moved);
        Assert.Equal("nupkg", Read("feeds/3/packages/pkg/1.0.0/pkg.1.0.0.nupkg"));
        Assert.Equal("pdb", Read("feeds/3/symbols/lib.pdb/abc/lib.pdb"));
        Assert.Equal("blob", Read("feeds/12/assets/ab/abcdef"));
        Assert.Equal("1 1", Read("feeds/12/asset-uploads/0123/manifest"));
        foreach (var area in (string[])["packages", "symbols", "assets", "asset-uploads"])
        {
            Assert.False(Directory.Exists(Path.Combine(root, area)), $"The emptied {area} folder was left behind.");
        }
    }

    /// <summary>
    /// A folder that cannot be moved this time - here because a file stands where its folder should go, the way another
    /// replica starting at the same moment can make the target appear between the check and the rename - is logged and
    /// left for the next start, and the rest still moves. It used to end the process.
    /// </summary>
    [Fact]
    public void A_folder_that_cannot_be_moved_now_does_not_stop_the_start()
    {
        Write("packages/blocked/pkg/1.0.0/pkg.1.0.0.nupkg", "blocked");
        Write("feeds/5/packages", "a file where the folder should be");
        Write("packages/modules/pkg/1.0.0/pkg.1.0.0.nupkg", "nupkg");

        var moved = StorageLayout.MoveNameFoldersToKeyFolders(root, [(5, "blocked"), (3, "modules")], NullLogger.Instance);

        Assert.Equal(1, moved);
        Assert.Equal("nupkg", Read("feeds/3/packages/pkg/1.0.0/pkg.1.0.0.nupkg"));
        Assert.Equal("blocked", Read("packages/blocked/pkg/1.0.0/pkg.1.0.0.nupkg"));
    }

    [Fact]
    public void A_second_run_finds_nothing_to_move()
    {
        Write("packages/modules/pkg/1.0.0/pkg.1.0.0.nupkg", "nupkg");
        StorageLayout.MoveNameFoldersToKeyFolders(root, [(3, "modules")], NullLogger.Instance);

        Assert.Equal(0, StorageLayout.MoveNameFoldersToKeyFolders(root, [(3, "modules")], NullLogger.Instance));
        Assert.Equal("nupkg", Read("feeds/3/packages/pkg/1.0.0/pkg.1.0.0.nupkg"));
    }

    /// <summary>
    /// A feed whose name is a number another feed's key is: the old layout's <c>packages/3</c> belongs to the feed named
    /// "3", and must not end up in the folder of the feed whose key is 3.
    /// </summary>
    [Fact]
    public void A_feed_named_like_another_feeds_key_keeps_its_own_files()
    {
        Write("packages/3/named-three/1.0.0/x.nupkg", "named 3");
        Write("packages/modules/keyed-three/1.0.0/x.nupkg", "key 3");

        StorageLayout.MoveNameFoldersToKeyFolders(root, [(3, "modules"), (9, "3")], NullLogger.Instance);

        Assert.Equal("named 3", Read("feeds/9/packages/named-three/1.0.0/x.nupkg"));
        Assert.Equal("key 3", Read("feeds/3/packages/keyed-three/1.0.0/x.nupkg"));
    }

    /// <summary>A start interrupted half way, or a file written to the new place already: nothing is overwritten.</summary>
    [Fact]
    public void Files_already_in_the_new_place_are_kept_and_the_rest_joins_them()
    {
        Write("packages/modules/a/1.0.0/a.nupkg", "old a");
        Write("packages/modules/b/1.0.0/b.nupkg", "old b");
        Write("feeds/3/packages/a/1.0.0/a.nupkg", "new a");

        StorageLayout.MoveNameFoldersToKeyFolders(root, [(3, "modules")], NullLogger.Instance);

        Assert.Equal("new a", Read("feeds/3/packages/a/1.0.0/a.nupkg"));
        Assert.Equal("old b", Read("feeds/3/packages/b/1.0.0/b.nupkg"));
        Assert.Equal("old a", Read("packages/modules/a/1.0.0/a.nupkg"));
    }

    [Fact]
    public void A_folder_no_feed_is_called_stays_where_it_is()
    {
        Write("packages/deleted-long-ago/pkg/1.0.0/pkg.nupkg", "orphan");

        Assert.Equal(0, StorageLayout.MoveNameFoldersToKeyFolders(root, [(3, "modules")], NullLogger.Instance));
        Assert.Equal("orphan", Read("packages/deleted-long-ago/pkg/1.0.0/pkg.nupkg"));
    }

    [Fact]
    public void A_storage_root_that_does_not_exist_yet_is_not_an_error() =>
        Assert.Equal(0, StorageLayout.MoveNameFoldersToKeyFolders(Path.Combine(root, "nothing"), [(3, "modules")], NullLogger.Instance));

    private void Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));
}
