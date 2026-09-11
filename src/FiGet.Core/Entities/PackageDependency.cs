namespace FiGet.Core.Entities;

/// <summary>
/// A dependency of a package version. A dependency group without dependencies is stored as one row
/// with a null <see cref="Id"/>, so empty target framework groups survive a round trip.
/// </summary>
public sealed class PackageDependency
{
    public long Key { get; set; }

    public long PackageVersionKey { get; set; }

    /// <summary>Order within the nuspec, so output matches input.</summary>
    public int Ordinal { get; set; }

    /// <summary>Short folder name, for example <c>net8.0</c>; empty for the "any" group.</summary>
    public string TargetFramework { get; set; } = "";

    public string? Id { get; set; }

    /// <summary>Normalised NuGet version range, for example <c>[1.0.0, )</c>; empty means any version.</summary>
    public string VersionRange { get; set; } = "";
}
