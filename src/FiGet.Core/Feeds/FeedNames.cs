using System.Text.RegularExpressions;

namespace FiGet.Core.Feeds;

public static partial class FeedNames
{
    /// <summary>Letters, digits, dot, dash and underscore; 1 to 64 characters; must start with a letter or digit.</summary>
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
