using FiGet.Core.Entities;

namespace FiGet.Web.Configuration;

/// <summary>The <c>FiGet</c> configuration section. Every key is documented in docs/configuration.md.</summary>
public sealed class FiGetOptions
{
    public const string SectionName = "FiGet";

    /// <summary>Absolute base URL used in every URL the protocols emit. Empty: derived from the request.</summary>
    public string? PublicBaseUrl { get; set; }

    public DatabaseOptions Database { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    /// <summary>Feeds created on start when missing. Existing feeds are never changed from configuration.</summary>
    public List<FeedSeedOptions> Feeds { get; set; } = [];

    public AuthOptions Auth { get; set; } = new();

    public LimitsOptions Limits { get; set; } = new();

    public ConnectorOptions Connector { get; set; } = new();

    public ThemingOptions Theming { get; set; } = new();
}

public sealed class ThemingOptions
{
    /// <summary>Name of the theme pack to serve, or empty for the built-in look.</summary>
    public string? Theme { get; set; }

    /// <summary>Where theme packs are read from. Default: the themes folder of the web root.</summary>
    public string? Path { get; set; }
}

public enum DatabaseProvider
{
    Sqlite,
    SqlServer,
}

public sealed class DatabaseOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Empty with SQLite: <c>Data Source={Storage.Root}/figet.db</c>. Required with SQL Server.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Apply pending migrations on start. EF Core takes a migration lock, so replicas can all do this.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>How many instances share this database. SQLite refuses to start when this is above 1.</summary>
    public int ExpectedReplicas { get; set; } = 1;
}

public enum StorageProvider
{
    FileSystem,
}

public sealed class StorageOptions
{
    public StorageProvider Provider { get; set; } = StorageProvider.FileSystem;

    /// <summary>Root directory for packages, symbols and (with SQLite) the database. Default: <c>data</c> under the content root.</summary>
    public string? Root { get; set; }

    /// <summary>Where uploads are buffered while they are validated. Default: the system temp directory.</summary>
    public string? TempPath { get; set; }
}

public sealed class FeedSeedOptions
{
    public string Name { get; set; } = "";

    public FeedKind Kind { get; set; } = FeedKind.Curated;

    public bool AnonymousRead { get; set; }

    public bool AllowOverwrite { get; set; }

    public PackageDeletionBehavior DeletionBehavior { get; set; } = PackageDeletionBehavior.Unlist;

    /// <summary>Upstreams to create with the feed. Only used when the feed itself is created.</summary>
    public List<UpstreamSeedOptions> Upstreams { get; set; } = [];
}

public sealed class UpstreamSeedOptions
{
    /// <summary>A name for logs and the UI, unique within the feed.</summary>
    public string Name { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>`V3` for a service index, `V2` for an OData feed root such as the PowerShell Gallery.</summary>
    public UpstreamKind Kind { get; set; } = UpstreamKind.V3;

    /// <summary>Regular expressions on the package id. Empty allows every id.</summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>Regular expressions on the package id. A match is never listed or fetched.</summary>
    public List<string> Deny { get; set; } = [];

    /// <summary>Environment variable holding the upstream's API key or password, never the secret itself.</summary>
    public string? CredentialRef { get; set; }
}

public sealed class ConnectorOptions
{
    /// <summary>How long an upstream's version list for one package stays usable before it is fetched again.</summary>
    public TimeSpan UpstreamIndexTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one upstream call may take before the upstream is treated as unavailable. Listing a
    /// package with hundreds of versions on a v2 gallery is a paged walk, not one request.
    /// </summary>
    public TimeSpan UpstreamTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class AuthOptions
{
    /// <summary>
    /// An admin token secret registered on start. When empty and no admin token exists, one is generated and
    /// written to the log once. Set this in clusters, from a secret, so every replica agrees.
    /// </summary>
    public string? BootstrapAdminToken { get; set; }
}

public sealed class LimitsOptions
{
    public int MaxPackageSizeMB { get; set; } = 256;
}
