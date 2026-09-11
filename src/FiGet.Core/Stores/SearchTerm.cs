namespace FiGet.Core.Stores;

/// <summary>One term of a parsed search query. <see cref="Value"/> is lower-cased and may contain <c>*</c> wildcards.</summary>
public sealed record SearchTerm(SearchField Field, string Value);

public enum SearchField
{
    /// <summary>Free text: matches id, title, tags, summary, description or authors.</summary>
    Any,

    /// <summary><c>id:</c> contains (or wildcard) match on the package id.</summary>
    Id,

    /// <summary><c>packageid:</c> exact, case-insensitive match on the package id.</summary>
    PackageId,

    /// <summary><c>tags:</c> or <c>tag:</c> exact token match within the tags.</summary>
    Tag,

    Title,

    Description,

    Author,
}
