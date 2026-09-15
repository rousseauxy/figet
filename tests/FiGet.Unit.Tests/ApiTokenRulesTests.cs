using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Unit.Tests;

/// <summary>The text rules behind API access tokens: which credentials go to the issuer check, and the provider's settings.</summary>
public sealed class ApiTokenRulesTests
{
    [Theory]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln", true)]
    [InlineData("figet_abcdefghijklmnopqrstuvwxyz0123456789", false)]
    // alg none leaves the signature empty: not the shape of a signed token, so it is looked up as a key and found nowhere.
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.", false)]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0", false)]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIi+OiJ4In0.c2ln", false)]
    [InlineData("a-person's-password", false)]
    [InlineData("", false)]
    public void Only_the_shape_of_a_signed_jwt_goes_to_the_issuer_check(string credential, bool expected) =>
        Assert.Equal(expected, IExternalTokenValidator.LooksLikeJwt(credential));

    [Fact]
    public void Required_claims_group_values_by_name()
    {
        var rules = OidcProvider.ParseRequiredClaims("ref_protected=true\r\n namespace_path = tools \nnamespace_path=platform\nref_protected=TRUE");

        Assert.NotNull(rules);
        Assert.Equal(["true"], rules["ref_protected"]);
        Assert.Equal(["tools", "platform"], rules["namespace_path"]);
    }

    [Theory]
    [InlineData("ref_protected")]
    [InlineData("=true")]
    [InlineData("ref_protected=")]
    [InlineData("ref_protected=true\nnamespace_path")]
    public void A_required_claim_that_is_not_name_equals_value_is_unreadable(string text) =>
        Assert.Null(OidcProvider.ParseRequiredClaims(text));

    [Fact]
    public void No_required_claims_is_an_empty_rule_set_and_audiences_are_one_per_line()
    {
        Assert.Empty(OidcProvider.ParseRequiredClaims("")!);
        Assert.Equal(["api://figet", "3f1c"], OidcProvider.ParseAudiences(" api://figet \r\n\r\n3f1c\napi://figet"));
    }
}
