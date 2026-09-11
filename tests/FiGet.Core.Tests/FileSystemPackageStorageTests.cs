using FiGet.Core.Storage;
using FiGet.Storage;

namespace FiGet.Core.Tests;

public sealed class FileSystemPackageStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "figet-storage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemPackageStorage storage;

    public FileSystemPackageStorageTests() => storage = new FileSystemPackageStorage(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Saves_opens_and_deletes_a_package()
    {
        var key = new PackageStorageKey("feed", "my.package", "1.0.0");
        using (var content = new MemoryStream([1, 2, 3]))
        {
            await storage.SavePackageAsync(key, content, "<package />"u8.ToArray(), overwrite: false, CancellationToken.None);
        }

        Assert.True(File.Exists(Path.Combine(root, "packages", "feed", "my.package", "1.0.0", "my.package.1.0.0.nupkg")));
        await using (var read = await storage.OpenPackageAsync(key, CancellationToken.None))
        {
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal([1, 2, 3], copy.ToArray());
        }

        await using (var nuspec = await storage.OpenNuspecAsync(key, CancellationToken.None))
        {
            Assert.NotNull(nuspec);
        }

        await storage.DeletePackageAsync(key, CancellationToken.None);
        Assert.Null(await storage.OpenPackageAsync(key, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, "packages", "feed", "my.package")));
    }

    [Fact]
    public async Task Refuses_to_replace_without_overwrite_and_replaces_with_it()
    {
        var key = new PackageStorageKey("feed", "pkg", "1.0.0");
        await storage.SavePackageAsync(key, new MemoryStream([1]), "<a/>"u8.ToArray(), overwrite: false, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => storage.SavePackageAsync(key, new MemoryStream([2]), "<a/>"u8.ToArray(), overwrite: false, CancellationToken.None));
        await storage.SavePackageAsync(key, new MemoryStream([3]), "<a/>"u8.ToArray(), overwrite: true, CancellationToken.None);

        await using var read = await storage.OpenPackageAsync(key, CancellationToken.None);
        Assert.Equal(3, read!.ReadByte());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "packages", "feed", "pkg", "1.0.0"), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_missing_file_opens_as_null()
    {
        Assert.Null(await storage.OpenPackageAsync(new PackageStorageKey("feed", "nothing", "1.0.0"), CancellationToken.None));
        Assert.Null(await storage.OpenSymbolAsync(new SymbolStorageKey("feed", "a.pdb", "0123"), CancellationToken.None));
    }

    [Theory]
    [InlineData("..", "pkg", "1.0.0")]
    [InlineData("feed", "..", "1.0.0")]
    [InlineData("feed", "a/b", "1.0.0")]
    [InlineData("feed", "a\\b", "1.0.0")]
    [InlineData("feed", "Upper", "1.0.0")]
    [InlineData("feed", "pkg", "")]
    public async Task Rejects_path_segments_that_could_escape_or_collide(string feed, string id, string version)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SavePackageAsync(new PackageStorageKey(feed, id, version), new MemoryStream([1]), "<a/>"u8.ToArray(), overwrite: true, CancellationToken.None));
    }

    [Fact]
    public async Task Saves_and_deletes_symbols()
    {
        var key = new SymbolStorageKey("feed", "lib.pdb", "0123456789abcdef0123456789abcdefffffffff");
        await storage.SaveSymbolAsync(key, new MemoryStream([9, 9]), CancellationToken.None);

        await using (var read = await storage.OpenSymbolAsync(key, CancellationToken.None))
        {
            Assert.NotNull(read);
        }

        await storage.DeleteSymbolAsync(key, CancellationToken.None);
        Assert.Null(await storage.OpenSymbolAsync(key, CancellationToken.None));
    }
}
