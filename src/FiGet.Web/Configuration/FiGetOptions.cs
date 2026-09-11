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
