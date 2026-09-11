using System.Text;
using FiGet.Core.Stores;

namespace FiGet.Core.Search;

/// <summary>
/// Parses the <c>q</c> parameter of the v3 search resource: whitespace-separated terms, optional
/// <c>field:value</c> prefixes, double quotes around values containing spaces.
/// </summary>
public static class SearchQueryParser
{
    private static readonly Dictionary<string, SearchField> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = SearchField.Id,
        ["packageid"] = SearchField.PackageId,
        ["tags"] = SearchField.Tag,
        ["tag"] = SearchField.Tag,
        ["title"] = SearchField.Title,
        ["description"] = SearchField.Description,
        ["author"] = SearchField.Author,
        ["authors"] = SearchField.Author,
    };

    public static IReadOnlyList<SearchTerm> Parse(string? query)
    {
        var terms = new List<SearchTerm>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return terms;
        }

        foreach (var token in Tokenize(query))
        {
            var field = SearchField.Any;
            var value = token;
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && Fields.TryGetValue(token[..colon], out var known))
            {
                field = known;
                value = token[(colon + 1)..];
            }

            value = value.Replace("\"", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
            if (value.Length == 0 || value.All(c => c == '*'))
            {
                continue;
            }

            terms.Add(new SearchTerm(field, value));
        }

        return terms;
    }

    private static IEnumerable<string> Tokenize(string query)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in query)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
