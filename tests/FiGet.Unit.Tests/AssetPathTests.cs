using FiGet.Domain.Assets;

namespace FiGet.Unit.Tests;

/// <summary>
/// Which paths an asset directory accepts. Every route a path arrives by goes through one parser, so this is
/// the whole of that rule, including the part that matters most: nothing that could walk out of a folder.
/// </summary>
public sealed class AssetPathTests
{
    [Theory]
    [InlineData("tools/setup.exe", "tools/setup.exe")]
    [InlineData("/tools//setup.exe/", "tools/setup.exe")]
    [InlineData("Tools/Runtime Installer 8.0.exe", "Tools/Runtime Installer 8.0.exe")]
    [InlineData("50%/a#b", "50%/a#b")]
    public void Accepts_and_normalises(string text, string expected)
    {
        Assert.True(AssetPath.TryParse(text, out var path));
        Assert.Equal(expected, path.Value);
        Assert.Equal(expected.ToLowerInvariant(), path.Lower);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a\\b")]
    [InlineData("a/\u0000/b")]
    [InlineData("a/   /b")]
    public void Refuses_anything_that_is_not_a_plain_name(string text)
    {
        Assert.False(AssetPath.TryParse(text, out _));
    }

    [Fact]
    public void Refuses_a_path_longer_than_the_database_can_index()
    {
        Assert.True(AssetPath.TryParse(new string('a', AssetPath.MaxSegmentLength), out _));
        Assert.False(AssetPath.TryParse(new string('a', AssetPath.MaxSegmentLength + 1), out _));
        Assert.False(AssetPath.TryParse(string.Join('/', Enumerable.Repeat(new string('a', 200), 3)), out _));
    }

    [Fact]
    public void Empty_is_the_root()
    {
        Assert.True(AssetPath.TryParse(null, out var none));
        Assert.True(none.IsRoot);
        Assert.True(AssetPath.TryParse("///", out var slashes));
        Assert.True(slashes.IsRoot);
        Assert.Equal("", slashes.Parent.Lower);
    }

    [Fact]
    public void Knows_its_parent_and_ancestors()
    {
        Assert.True(AssetPath.TryParse("A/b/C.txt", out var path));
        Assert.Equal("C.txt", path.Name);
        Assert.Equal("a/b", path.Parent.Lower);
        Assert.Equal(["A", "A/b"], path.Ancestors().Select(a => a.Value));
    }
}
