namespace FiGet.Domain.Entities;

/// <summary>Which NuGet protocol an upstream speaks. The URL alone cannot always tell.</summary>
public enum UpstreamKind
{
    /// <summary>An OData v2 feed root, for example the PowerShell Gallery's <c>/api/v2</c>.</summary>
    V2,

    /// <summary>A v3 service index, for example <c>https://api.nuget.org/v3/index.json</c>.</summary>
    V3,
}

/// <summary>
/// One upstream of a proxy feed (build plan section 5). A feed may have several, tried in
/// <see cref="Ordinal"/> order; the first that holds a given version serves the download.
/// </summary>
public sealed class FeedUpstream
{
    public int Key { get; set; }

    public int FeedKey { get; set; }

    public Feed? Feed { get; set; }

    /// <summary>Order within the feed, so results and downloads are deterministic.</summary>
    public int Ordinal { get; set; }

    /// <summary>A name for logs and the UI, unique within the feed.</summary>
    public required string Name { get; set; }

    public required string Url { get; set; }

    public UpstreamKind Kind { get; set; } = UpstreamKind.V3;

    /// <summary>When false the upstream is kept but not queried, which is how an outage is handled by hand.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Separates the patterns in <see cref="Allow"/> and <see cref="Deny"/>.</summary>
    public const char PatternSeparator = '\n';

    /// <summary>
    /// Regular expressions matched against the package id, separated by <see cref="PatternSeparator"/>.
    /// Empty allows everything; otherwise an id must match one of them to be listed or fetched.
    /// </summary>
    public string Allow { get; set; } = "";

    /// <summary>Regular expressions like <see cref="Allow"/>; a matching id is never listed or fetched.</summary>
    public string Deny { get; set; } = "";

    /// <summary>The patterns of one of the two lists, without the empty entries.</summary>
    public static string[] Patterns(string value) =>
        value.Split(PatternSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Name of the environment variable or mounted file holding an API key or password for this upstream.
    /// The secret itself is never stored in the database (build plan section 8).
    /// </summary>
    public string? CredentialRef { get; set; }
}
