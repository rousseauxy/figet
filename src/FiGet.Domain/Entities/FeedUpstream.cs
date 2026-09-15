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
    /// Name of the environment variable holding an API key or password for this upstream, always starting with
    /// <see cref="CredentialPrefix"/>. The secret itself is never stored in the database (build plan section 8).
    /// </summary>
    public string? CredentialRef { get; set; }

    /// <summary>
    /// The only environment variables an upstream may name. Without it the field read any variable of the process - the
    /// database connection string, the bootstrap token - and sent its value as a password to the upstream's URL.
    /// </summary>
    public const string CredentialPrefix = "FIGET_UPSTREAM_";

    /// <summary>Empty, or the prefix followed by upper-case letters, digits and underscores.</summary>
    public static bool IsAllowedCredentialRef(string? name) =>
        string.IsNullOrEmpty(name)
        || (name.Length > CredentialPrefix.Length
            && name.Length <= 128
            && name.StartsWith(CredentialPrefix, StringComparison.Ordinal)
            && name.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'));

    /// <summary>
    /// The public galleries a feed manager may add without an admin. Any other URL, and every credential, is an admin's
    /// choice: an upstream is a request the server makes, from inside its network, with the credential it is given.
    /// </summary>
    public static IReadOnlyList<KnownUpstream> Known { get; } =
    [
        new("nuget.org", "https://api.nuget.org/v3/index.json", UpstreamKind.V3),
        new("PowerShell Gallery", "https://www.powershellgallery.com/api/v2", UpstreamKind.V2),
        new("Chocolatey community", "https://community.chocolatey.org/api/v2", UpstreamKind.V2),
    ];

    /// <summary>The public gallery for a feed used for <paramref name="purpose"/>; null when there is no one gallery.</summary>
    public static KnownUpstream? KnownFor(FeedPurpose purpose) => purpose switch
    {
        FeedPurpose.PowerShell => Known[1],
        FeedPurpose.NuGet => Known[0],
        FeedPurpose.Chocolatey => Known[2],
        _ => null,
    };

    /// <summary>Whether the upstream's source - where it points and with what - differs from another's.</summary>
    public bool SourceDiffersFrom(FeedUpstream other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Url != other.Url || Kind != other.Kind || CredentialRef != other.CredentialRef;
    }
}

/// <summary>A public gallery offered by name on the upstream form.</summary>
public sealed record KnownUpstream(string Name, string Url, UpstreamKind Kind);
