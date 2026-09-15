using System.Text.RegularExpressions;
using FiGet.Domain.Entities;

namespace FiGet.Domain.Feeds;

public static partial class FeedNames
{
    /// <summary>Letters, digits, dot, dash and underscore; 1 to 64 characters; must start with a letter or digit.</summary>
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    /// <summary>
    /// Why a valid name will still trip up a client, or null. A package feed named <c>nuget</c> has an address ending in
    /// <c>/nuget</c>, which PSResourceGet takes for a NuGet.Server feed: in that mode exact versions find nothing and downloads
    /// ask for a route of its own (PSResourceGet #1206, #1896).
    /// </summary>
    public static string? ClientWarning(string? name, FeedKind kind) =>
        kind != FeedKind.Assets && string.Equals(name, "nuget", StringComparison.OrdinalIgnoreCase)
            ? "PSResourceGet treats an address ending in /nuget as a NuGet.Server feed, where exact versions find nothing. Register PowerShell 7 at this feed's /api/v2 address, as its page shows, or choose another name."
            : null;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
