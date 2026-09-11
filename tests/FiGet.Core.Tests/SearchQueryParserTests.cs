using FiGet.Core.Search;
using FiGet.Core.Stores;

namespace FiGet.Core.Tests;

public sealed class SearchQueryParserTests
{
    [Fact]
    public void Free_text_terms_are_lower_cased()
    {
        var terms = SearchQueryParser.Parse("Pester  Helper");

        Assert.Equal([new SearchTerm(SearchField.Any, "pester"), new SearchTerm(SearchField.Any, "helper")], terms);
    }

    [Theory]
    [InlineData("id:Az.Accounts", SearchField.Id, "az.accounts")]
    [InlineData("packageid:Az.Accounts", SearchField.PackageId, "az.accounts")]
    [InlineData("PackageId:Az.Accounts", SearchField.PackageId, "az.accounts")]
    [InlineData("tags:PSModule", SearchField.Tag, "psmodule")]
    [InlineData("tag:PSModule", SearchField.Tag, "psmodule")]
    [InlineData("description:graph", SearchField.Description, "graph")]
    [InlineData("author:Microsoft", SearchField.Author, "microsoft")]
    public void Field_prefixes_are_recognised(string query, SearchField field, string value)
    {
        Assert.Equal(new SearchTerm(field, value), Assert.Single(SearchQueryParser.Parse(query)));
    }

    [Fact]
    public void Quoted_values_keep_their_spaces()
    {
        Assert.Equal(new SearchTerm(SearchField.Description, "active directory"), Assert.Single(SearchQueryParser.Parse("description:\"Active Directory\"")));
    }

    [Fact]
    public void Unknown_prefixes_are_free_text()
    {
        Assert.Equal(new SearchTerm(SearchField.Any, "owner:someone"), Assert.Single(SearchQueryParser.Parse("owner:someone")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("id:*")]
    public void Empty_and_match_everything_queries_have_no_terms(string? query)
    {
        Assert.Empty(SearchQueryParser.Parse(query));
    }

    [Fact]
    public void Wildcards_are_kept_for_the_store_to_translate()
    {
        Assert.Equal(new SearchTerm(SearchField.Id, "az.*"), Assert.Single(SearchQueryParser.Parse("id:Az.*")));
    }
}
