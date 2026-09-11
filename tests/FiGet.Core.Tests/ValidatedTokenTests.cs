using FiGet.Core.Entities;
using FiGet.Core.Feeds;
using FiGet.Core.Tokens;

namespace FiGet.Core.Tests;

public sealed class ValidatedTokenTests
{
    [Fact]
    public void Push_and_delete_imply_read()
    {
        Assert.True(new ValidatedToken(1, "t", TokenScopes.Push, null).Allows(TokenScopes.Read, 7));
        Assert.True(new ValidatedToken(1, "t", TokenScopes.Delete, null).Allows(TokenScopes.Read, 7));
        Assert.False(new ValidatedToken(1, "t", TokenScopes.Read, null).Allows(TokenScopes.Push, 7));
        Assert.False(new ValidatedToken(1, "t", TokenScopes.Push, null).Allows(TokenScopes.Delete, 7));
    }

    [Fact]
    public void A_feed_scoped_token_only_works_on_its_feed()
    {
        var token = new ValidatedToken(1, "t", TokenScopes.Push, FeedKey: 3);

        Assert.True(token.Allows(TokenScopes.Push, 3));
        Assert.False(token.Allows(TokenScopes.Read, 4));
    }

    [Fact]
    public void Admin_allows_everything_everywhere()
    {
        var token = new ValidatedToken(1, "t", TokenScopes.Admin, FeedKey: 3);

        Assert.True(token.Allows(TokenScopes.Delete, 99));
    }

    [Fact]
    public void Hashes_are_stable_lower_case_hex()
    {
        var hash = AccessTokenService.HashSecret("figet_example");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Equal(hash, AccessTokenService.HashSecret("figet_example"));
    }

    [Theory]
    [InlineData("modules", true)]
    [InlineData("PowerShellModules", true)]
    [InlineData("a.b-c_d", true)]
    [InlineData("", false)]
    [InlineData(".hidden", false)]
    [InlineData("has space", false)]
    [InlineData("slash/name", false)]
    public void Feed_names_are_validated(string name, bool valid)
    {
        Assert.Equal(valid, FeedNames.IsValid(name));
    }
}
