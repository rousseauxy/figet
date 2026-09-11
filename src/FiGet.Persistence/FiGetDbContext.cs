using FiGet.Core.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FiGet.Persistence;

/// <summary>
/// Provider-neutral model. Migrations live in FiGet.Persistence.SqlServer and FiGet.Persistence.Sqlite.
/// Rules that keep both providers identical: lookups use lower-cased columns (no collation dependence),
/// dates are UTC <see cref="DateTime"/> (SQLite cannot order DateTimeOffset), enums are stored as strings.
/// </summary>
public sealed class FiGetDbContext(DbContextOptions<FiGetDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Feed> Feeds => Set<Feed>();

    public DbSet<Package> Packages => Set<Package>();

    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();

    public DbSet<PackageDependency> PackageDependencies => Set<PackageDependency>();

    public DbSet<SymbolFile> SymbolFiles => Set<SymbolFile>();

    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

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
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
