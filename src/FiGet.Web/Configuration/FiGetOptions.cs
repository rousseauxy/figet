using FiGet.Application.Connectors;
using FiGet.Domain.Entities;

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

    public AssetOptions Assets { get; set; } = new();

    public AuditOptions Audit { get; set; } = new();

    public JobsOptions Jobs { get; set; } = new();

    public DataProtectionSettings DataProtection { get; set; } = new();

    /// <summary>Per-address limits on what can be done without a key or a sign-in.</summary>
    public FiGet.Http.RateLimitOptions RateLimits { get; set; } = new();

    /// <summary>
    /// Compress protocol answers (<c>/nuget</c> and <c>/api/packages</c>) when the client accepts it. On by default:
    /// one <c>Find-Module</c> of a package with thousands of versions is about 80 MB of Atom, and a server on a slow
    /// line waits for every byte. Turn it off when a reverse proxy in front already compresses.
    /// </summary>
    public bool CompressProtocolResponses { get; set; } = true;

    /// <summary>
    /// What to show as the running version, for example the image tag a deployment built. Empty: the
    /// assembly's informational version, which is what a local run has.
    /// </summary>
    public string? Version { get; set; }
}

public sealed class DataProtectionSettings
{
    /// <summary>
    /// 32 random bytes in base64 that encrypt the data-protection key ring in the database. From a secret, never a file
    /// in the repository. Required with more than one replica.
    /// </summary>
    public string? MasterKey { get; set; }
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

    /// <summary>
    /// Asset directories: whether folders can be listed without credentials. Null, the default, follows
    /// <see cref="AnonymousRead"/>, which is how a directory behaved before the switch existed.
    /// </summary>
    public bool? AnonymousList { get; set; }

    /// <summary>
    /// Asset directories: a folder on the server whose content the directory serves as it is - a mounted share. Applied on
    /// every start, so a moved mount is followed; it is the operator's, never a page's.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>Folder-backed directories: whether uploads, folders and deletes through FiGet act on the folder. Off by default.</summary>
    public bool FolderWrites { get; set; }

    public bool AllowOverwrite { get; set; }

    public PackageDeletionBehavior DeletionBehavior { get; set; } = PackageDeletionBehavior.Unlist;

    /// <summary>Package feeds: which clients the pages show commands for (Any, PowerShell, NuGet, Chocolatey). Only used when the feed is created.</summary>
    public FeedPurpose Purpose { get; set; } = FeedPurpose.Any;

    /// <summary>
    /// When true, an id pushed to this feed is still merged with the same id on its upstreams. Off by default: a
    /// pushed id is served only from this feed, so a same-named package elsewhere cannot become its latest version.
    /// </summary>
    public bool MergePushedIdsWithUpstreams { get; set; }

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

    /// <summary>
    /// How many package ids one replica keeps upstream descriptions for in memory before dropping the
    /// oldest. The descriptions are the large part - a couple of thousand versions of one module can be
    /// a hundred megabytes of tags - and every replica holds its own copy, so this is the setting that
    /// decides the memory a busy instance settles at. Zero or less means the default.
    ///
    /// Lowering it costs listings their description text until the next refresh, not their correctness:
    /// what a client needs to resolve a package is in the database.
    /// </summary>
    public int MaxDescribedPackages { get; set; } = UpstreamMetadataCache.DefaultMaxPackages;
}

public sealed class AuthOptions
{
    /// <summary>
    /// A service token with admin scope registered on start, for automation that must work before anyone has signed in.
    /// It does not sign in to the web UI; people use accounts.
    /// </summary>
    public string? BootstrapAdminToken { get; set; }

    /// <summary>Resets an account on start when nobody can sign in any more. Remove after use.</summary>
    public RecoveryOptions Recovery { get; set; } = new();
}

public sealed class RecoveryOptions
{
    /// <summary>The account to recover; created when it does not exist.</summary>
    public string? UserName { get; set; }

    /// <summary>A password to sign in with once; a new one is required straight after.</summary>
    public string? Password { get; set; }
}

public sealed class LimitsOptions
{
    public int MaxPackageSizeMB { get; set; } = 256;

    /// <summary>The largest file an asset directory accepts. Sized for installers, not packages.</summary>
    public int MaxAssetSizeMB { get; set; } = 1024;

    /// <summary>The largest archive an import accepts, and the most it may unpack to.</summary>
    public int MaxImportSizeMB { get; set; } = 4096;
}

/// <summary>
/// How often each background job runs. Every default is what that job did before these settings existed, so an
/// instance that sets none of them behaves exactly as it did.
///
/// A zero or negative interval switches the job off: its hosted service is not registered at all, so nothing spins
/// and nothing takes a lease. That is for an operator who wants a job somewhere else - a single replica of many
/// doing the pruning, a maintenance window - not a way to make a job cheaper.
///
/// The interval is also how long the runner holds the job's lease, so a longer one widens the window in which a
/// single replica owns the job.
/// </summary>
public sealed class JobsOptions
{
    /// <summary>Retention and cache pruning, feed by feed.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Deleting audit entries past <see cref="AuditOptions.RetentionDays"/>.</summary>
    public TimeSpan AuditPrune { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Removing multipart uploads nobody finished.</summary>
    public TimeSpan UploadSweep { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Deleting usage counts past their retention.</summary>
    public TimeSpan UsagePrune { get; set; } = TimeSpan.FromDays(1);

    public static bool Runs(TimeSpan interval) => interval > TimeSpan.Zero;
}

public sealed class AuditOptions
{
    /// <summary>
    /// Audit entries older than this many days are deleted, checked every few hours. 0 keeps them forever; the server
    /// being replaced does that, and its own documentation then tells administrators to purge a table grown to gigabytes.
    /// </summary>
    public int RetentionDays { get; set; } = 365;
}

public sealed class AssetOptions
{
    /// <summary>How long the parts of a multipart upload wait for their completion before they are removed.</summary>
    public TimeSpan IncompleteUploadExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// A folder on the server whose direct sub-folders an administrator may choose, on the pages, as the content of an
    /// asset directory: one mount per sub-folder (a compose volume, a PVC), for example <c>/shares</c>. The pages offer
    /// names from this folder and never take a path, so nothing outside it can be served. Empty: the pages offer no
    /// folders, and a folder-backed directory comes only from <c>Feeds:N:Folder</c>.
    /// </summary>
    public string? SharesRoot { get; set; }

    public RemoteFetchOptions RemoteFetch { get; set; } = new();
}

public sealed class RemoteFetchOptions
{
    /// <summary>How long fetching one file from a URL may take, the download included.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether a URL may point into a private network or at this host. Off by default, because a token that
    /// may upload could otherwise make the server read internal addresses and store the answer where the
    /// token can download it. Cloud metadata addresses stay refused either way.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>An HTTP proxy to fetch through, for an instance whose only way out is one. Needs <see cref="AllowedHosts"/>.</summary>
    public string? Proxy { get; set; }

    /// <summary>Hosts a fetch may go to, redirects included (<c>download.example.com</c>, <c>*.example.com</c>). Empty: any, without a proxy.</summary>
    public List<string> AllowedHosts { get; set; } = [];
}
