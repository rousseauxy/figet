namespace FiGet.Infrastructure.Packages;

/// <summary>
/// The lengths of the package columns that stay bounded, shared by the database model and the indexer. A nuspec value
/// longer than its column is refused at indexing with a message that names it, rather than failing inside the insert,
/// where SQL Server refuses a truncation and SQLite, which enforces no lengths, stores it: the same package would work on
/// one provider and not the other. Free text that real packages make long - tags, authors, summary, copyright,
/// description, release notes - is unbounded instead.
/// </summary>
public static class PackageColumnLimits
{
    public const int Title = 512;

    public const int Url = 2048;

    public const int LicenseExpression = 512;

    public const int RepositoryType = 64;

    public const int Language = 64;

    public const int MinClientVersion = 64;

    /// <summary>The package types as stored: joined with and wrapped in <c>|</c>.</summary>
    public const int PackageTypes = 512;

    public const int DependencyId = 128;

    public const int DependencyVersionRange = 256;

    public const int TargetFramework = 64;
}
