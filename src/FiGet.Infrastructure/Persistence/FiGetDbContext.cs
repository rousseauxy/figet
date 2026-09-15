using FiGet.Domain.Entities;
using FiGet.Domain.Feeds;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FiGet.Infrastructure.Persistence;

/// <summary>
/// Provider-neutral model. Migrations live in FiGet.Infrastructure.SqlServer and FiGet.Infrastructure.Sqlite.
/// Rules that keep both providers identical: lookups use lower-cased columns (no collation dependence),
/// dates are UTC <see cref="DateTime"/> (SQLite cannot order DateTimeOffset), enums are stored as strings.
/// </summary>
public sealed class FiGetDbContext(DbContextOptions<FiGetDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Feed> Feeds => Set<Feed>();

    public DbSet<FeedAlias> FeedAliases => Set<FeedAlias>();

    public DbSet<FeedName> Names => Set<FeedName>();

    public DbSet<Package> Packages => Set<Package>();

    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();

    public DbSet<PackageDependency> PackageDependencies => Set<PackageDependency>();

    public DbSet<SymbolFile> SymbolFiles => Set<SymbolFile>();

    public DbSet<FeedUpstream> FeedUpstreams => Set<FeedUpstream>();

    public DbSet<CachedUpstreamIndex> CachedUpstreamIndexes => Set<CachedUpstreamIndex>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<OidcProvider> OidcProviders => Set<OidcProvider>();

    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();

    public DbSet<GroupProviderLink> GroupProviderLinks => Set<GroupProviderLink>();

    public DbSet<FeedPermission> FeedPermissions => Set<FeedPermission>();

    public DbSet<CachedUpstreamDescription> CachedUpstreamDescriptions => Set<CachedUpstreamDescription>();

    public DbSet<CachedUpstreamTagSet> CachedUpstreamTagSets => Set<CachedUpstreamTagSet>();

    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<JobLease> JobLeases => Set<JobLease>();

    public DbSet<FeedUsage> FeedUsage => Set<FeedUsage>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<AssetItem> AssetItems => Set<AssetItem>();

    public DbSet<AssetCachePolicy> AssetCachePolicies => Set<AssetCachePolicy>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Feed>(e =>
        {
            e.ToTable("Feeds");
            e.HasKey(x => x.Key);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.NameLower).HasMaxLength(64);
            e.HasIndex(x => x.NameLower).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DeletionBehavior).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ClientBaseUrl).HasMaxLength(512);
            e.Property(x => x.FolderRoot).HasMaxLength(1024);
            e.Ignore(x => x.IsFolderBacked);
            e.Property(x => x.AllowedNetworks).HasMaxLength(FeedNetworks.MaxLength);
            e.Property(x => x.PackageInstructions).HasMaxLength(4000);
            e.Property(x => x.FeedInstructions).HasMaxLength(4000);
            e.Property(x => x.FileInstructions).HasMaxLength(4000);
        });

        // The one set of names across feeds and alternate names. Its primary key is the rule: the two tables below each
        // have their own unique index, and this is what makes a name unique across both.
        modelBuilder.Entity<FeedName>(e =>
        {
            e.ToTable("Names");
            e.HasKey(x => x.NameLower);
            e.Property(x => x.NameLower).HasMaxLength(64);
            e.HasIndex(x => x.FeedKey);
            e.HasOne<Feed>().WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FeedAlias>(e =>
        {
            e.ToTable("FeedAliases");
            e.HasKey(x => x.Key);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.NameLower).HasMaxLength(64);
            e.HasIndex(x => x.NameLower).IsUnique();
            e.HasIndex(x => x.FeedKey);
            e.HasOne<Feed>().WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FeedUpstream>(e =>
        {
            e.ToTable("FeedUpstreams");
            e.HasKey(x => x.Key);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Allow).HasMaxLength(4000);
            e.Property(x => x.Deny).HasMaxLength(4000);
            e.Property(x => x.CredentialRef).HasMaxLength(128);
            e.HasIndex(x => new { x.FeedKey, x.Name }).IsUnique();
            e.HasOne(x => x.Feed).WithMany(x => x.Upstreams).HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FeedUsage>(e =>
        {
            e.ToTable("FeedUsage");
            e.HasKey(x => new { x.FeedKey, x.HourUtc, x.Kind });
            e.HasIndex(x => x.HourUtc);
            e.HasOne<Feed>().WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<JobLease>(e =>
        {
            e.ToTable("JobLeases");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Holder).HasMaxLength(128);
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.ToTable("Settings");
            // The key is the primary key: there is exactly one row per setting, and no surrogate to
            // keep unique alongside it.
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(1024);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
        });

        modelBuilder.Entity<CachedUpstreamIndex>(e =>
        {
            e.ToTable("CachedUpstreamIndexes");
            e.HasKey(x => x.Key);
            e.Property(x => x.IdLower).HasMaxLength(128);
            e.Property(x => x.Id).HasMaxLength(128).IsRequired().HasDefaultValue("");
            e.Property(x => x.UnlistedVersions).IsRequired().HasDefaultValue("");
            e.Property(x => x.Dependencies).IsRequired().HasDefaultValue("");
            e.HasIndex(x => new { x.FeedUpstreamKey, x.IdLower }).IsUnique();
            e.HasOne(x => x.FeedUpstream).WithMany().HasForeignKey(x => x.FeedUpstreamKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Key);
            e.Property(x => x.UserName).HasMaxLength(64);
            e.Property(x => x.UserNameLower).HasMaxLength(64);
            e.HasIndex(x => x.UserNameLower).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
        });

        modelBuilder.Entity<Group>(e =>
        {
            e.ToTable("Groups");
            e.HasKey(x => x.Key);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.NameLower).HasMaxLength(64);
            e.HasIndex(x => x.NameLower).IsUnique();
            e.Property(x => x.Description).HasMaxLength(512);
        });

        modelBuilder.Entity<GroupMember>(e =>
        {
            e.ToTable("GroupMembers");
            e.HasKey(x => new { x.GroupKey, x.UserKey });
            e.HasIndex(x => x.UserKey);
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupKey).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserKey).OnDelete(DeleteBehavior.Cascade);

            // Deleted by EfOidcProviderStore with the provider; a third cascade into this table is one SQL Server may refuse.
            e.HasOne<OidcProvider>().WithMany().HasForeignKey(x => x.ProviderKey).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries");
            e.HasKey(x => x.Key);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Subject).HasMaxLength(256);
            e.Property(x => x.Actor).HasMaxLength(192);
            e.Property(x => x.ActorLower).HasMaxLength(192);
            e.Property(x => x.Feed).HasMaxLength(64);
            e.Property(x => x.FeedLower).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.Caller).HasMaxLength(128);
            e.HasIndex(x => x.WhenUtc);
            e.HasIndex(x => new { x.FeedLower, x.Key });
            e.HasIndex(x => new { x.ActorLower, x.Key });
            e.HasIndex(x => new { x.Action, x.Key });
        });

        modelBuilder.Entity<OidcProvider>(e =>
        {
            e.ToTable("OidcProviders");
            e.HasKey(x => x.Key);
            e.Property(x => x.Slug).HasMaxLength(32);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(64);
            e.Property(x => x.Authority).HasMaxLength(512);
            e.Property(x => x.ClientId).HasMaxLength(256);
            e.Property(x => x.ProtectedClientSecret).HasMaxLength(2048);
            e.Property(x => x.Scopes).HasMaxLength(512);
            e.Property(x => x.UserNameClaim).HasMaxLength(64);
            e.Property(x => x.GroupsClaim).HasMaxLength(64);
            e.Property(x => x.AllowedEmailDomains).HasMaxLength(1024);
            e.Property(x => x.ApiAudiences).HasMaxLength(1024);
            e.Property(x => x.ApiRequiredClaims).HasMaxLength(1024);
        });

        modelBuilder.Entity<ExternalLogin>(e =>
        {
            e.ToTable("ExternalLogins");
            e.HasKey(x => x.Key);
            e.Property(x => x.Subject).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(256);
            e.HasIndex(x => new { x.ProviderKey, x.Subject }).IsUnique();
            e.HasIndex(x => x.UserKey);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserKey).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OidcProvider>().WithMany().HasForeignKey(x => x.ProviderKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GroupProviderLink>(e =>
        {
            e.ToTable("GroupProviderLinks");
            e.HasKey(x => x.Key);
            e.Property(x => x.ProviderGroup).HasMaxLength(256);
            e.HasIndex(x => new { x.GroupKey, x.ProviderKey, x.ProviderGroup }).IsUnique();
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupKey).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OidcProvider>().WithMany().HasForeignKey(x => x.ProviderKey).OnDelete(DeleteBehavior.Cascade);
        });

        // Deleted explicitly by the stores rather than by cascade from feeds, users and groups at once: SQL Server refuses
        // a table reachable by more than one cascade path. The feed's own cascade is the one kept.
        modelBuilder.Entity<FeedPermission>(e =>
        {
            e.ToTable("FeedPermissions");
            e.HasKey(x => x.Key);
            e.HasIndex(x => new { x.FeedKey, x.UserKey });
            e.HasIndex(x => new { x.FeedKey, x.GroupKey });
            e.HasIndex(x => x.UserKey);
            e.HasIndex(x => x.GroupKey);
            e.HasOne<Feed>().WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserKey).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupKey).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<CachedUpstreamDescription>(e =>
        {
            e.ToTable("CachedUpstreamDescriptions");
            e.HasKey(x => x.Key);
            e.Property(x => x.IdLower).HasMaxLength(128);
            e.Property(x => x.NormalizedVersion).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Authors).HasMaxLength(1024);
            e.Property(x => x.ProjectUrl).HasMaxLength(2048);
            e.Property(x => x.IconUrl).HasMaxLength(2048);
            e.Property(x => x.LicenseUrl).HasMaxLength(2048);
            e.Property(x => x.TagSetHash).HasMaxLength(64);
            e.HasIndex(x => new { x.FeedUpstreamKey, x.IdLower, x.NormalizedVersion }).IsUnique();
            e.HasIndex(x => x.TagSetHash);
            e.HasOne(x => x.FeedUpstream).WithMany().HasForeignKey(x => x.FeedUpstreamKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CachedUpstreamTagSet>(e =>
        {
            e.ToTable("CachedUpstreamTagSets");
            e.HasKey(x => x.Hash);
            e.Property(x => x.Hash).HasMaxLength(64);
        });

        modelBuilder.Entity<Package>(e =>
        {
            e.ToTable("Packages");
            e.HasKey(x => x.Key);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.IdLower).HasMaxLength(128);
            e.HasIndex(x => new { x.FeedKey, x.IdLower }).IsUnique();
            e.HasOne(x => x.Feed).WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Versions).WithOne(x => x.Package).HasForeignKey(x => x.PackageKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PackageVersion>(e =>
        {
            e.ToTable("PackageVersions");
            e.HasKey(x => x.Key);
            e.Property(x => x.OriginalVersion).HasMaxLength(128);
            e.Property(x => x.NormalizedVersion).HasMaxLength(64);
            e.Property(x => x.NormalizedVersionLower).HasMaxLength(64);
            e.HasIndex(x => new { x.PackageKey, x.NormalizedVersionLower }).IsUnique();
            e.Property(x => x.Origin).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Authors).HasMaxLength(4000);
            e.Property(x => x.Summary).HasMaxLength(4000);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Tags).HasMaxLength(4000);
            e.Property(x => x.TagsLower).HasMaxLength(4000);
            e.Property(x => x.IconUrl).HasMaxLength(2048);
            e.Property(x => x.LicenseUrl).HasMaxLength(2048);
            e.Property(x => x.LicenseExpression).HasMaxLength(512);
            e.Property(x => x.ProjectUrl).HasMaxLength(2048);
            e.Property(x => x.RepositoryUrl).HasMaxLength(2048);
            e.Property(x => x.RepositoryType).HasMaxLength(64);
            e.Property(x => x.Copyright).HasMaxLength(4000);
            e.Property(x => x.Language).HasMaxLength(64);
            e.Property(x => x.MinClientVersion).HasMaxLength(64);
            e.Property(x => x.PackageTypes).HasMaxLength(512);
            e.Property(x => x.PackageTypesLower).HasMaxLength(512);
            e.Property(x => x.Hash).HasMaxLength(128);
            e.Property(x => x.HashAlgorithm).HasMaxLength(16);
            e.HasMany(x => x.Dependencies).WithOne().HasForeignKey(x => x.PackageVersionKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PackageDependency>(e =>
        {
            e.ToTable("PackageDependencies");
            e.HasKey(x => x.Key);
            e.Property(x => x.TargetFramework).HasMaxLength(64);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.VersionRange).HasMaxLength(256);
            e.HasIndex(x => x.PackageVersionKey);
        });

        modelBuilder.Entity<SymbolFile>(e =>
        {
            e.ToTable("SymbolFiles");
            e.HasKey(x => x.Key);
            e.Property(x => x.FileNameLower).HasMaxLength(256);
            e.Property(x => x.SymbolKeyLower).HasMaxLength(64);
            e.HasIndex(x => new { x.FeedKey, x.FileNameLower, x.SymbolKeyLower });
            e.HasIndex(x => x.PackageVersionKey);
            e.HasOne(x => x.PackageVersion).WithMany().HasForeignKey(x => x.PackageVersionKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssetItem>(e =>
        {
            e.ToTable("AssetItems");
            e.HasKey(x => x.Key);
            e.Property(x => x.Path).HasMaxLength(FiGet.Domain.Assets.AssetPath.MaxLength);
            e.Property(x => x.PathLower).HasMaxLength(FiGet.Domain.Assets.AssetPath.MaxLength);
            e.Property(x => x.ParentLower).HasMaxLength(FiGet.Domain.Assets.AssetPath.MaxLength);
            e.Property(x => x.Name).HasMaxLength(FiGet.Domain.Assets.AssetPath.MaxSegmentLength);
            e.Property(x => x.BlobId).HasMaxLength(32);
            e.Property(x => x.ContentType).HasMaxLength(256);
            e.Property(x => x.Md5).HasMaxLength(32);
            e.Property(x => x.Sha1).HasMaxLength(40);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Sha512).HasMaxLength(128);
            e.Property(x => x.CacheHeaderType).HasMaxLength(32);
            e.Property(x => x.CacheHeaderValue).HasMaxLength(256);
            e.HasIndex(x => new { x.FeedKey, x.PathLower }).IsUnique();
            e.HasIndex(x => new { x.FeedKey, x.ParentLower });
            e.HasOne(x => x.Feed).WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssetCachePolicy>(e =>
        {
            e.ToTable("AssetCachePolicies");
            e.HasKey(x => x.Key);
            e.Property(x => x.PathLower).HasMaxLength(FiGet.Domain.Assets.AssetPath.MaxLength);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.FeedKey, x.PathLower }).IsUnique();
            e.HasOne<Feed>().WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessToken>(e =>
        {
            e.ToTable("AccessTokens");
            e.HasKey(x => x.Key);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.Prefix).HasMaxLength(32);
            e.HasIndex(x => x.Hash).IsUnique();
            e.Property(x => x.Scopes).HasConversion<int>();
            e.HasOne(x => x.Feed).WithMany().HasForeignKey(x => x.FeedKey).OnDelete(DeleteBehavior.Cascade);

            // Deleted by EfUserStore together with the account, as its grants and memberships are.
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserKey).OnDelete(DeleteBehavior.NoAction);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
