using NuGet.Versioning;

namespace FiGet.Domain.Packages;

/// <summary>Everything read from a nupkg: nuspec metadata, the raw nuspec bytes, size and hash.</summary>
public sealed record IndexedPackage
{
    public required string Id { get; init; }

    public required NuGetVersion Version { get; init; }

    public required string OriginalVersion { get; init; }

    public required bool IsSemVer2 { get; init; }

    public string Authors { get; init; } = "";

    public string Description { get; init; } = "";

    public string Summary { get; init; } = "";

    public string Title { get; init; } = "";

    public string Tags { get; init; } = "";

    public string IconUrl { get; init; } = "";

    public string LicenseUrl { get; init; } = "";

    public string LicenseExpression { get; init; } = "";

    public string ProjectUrl { get; init; } = "";

    public string RepositoryUrl { get; init; } = "";

    public string RepositoryType { get; init; } = "";

    public string ReleaseNotes { get; init; } = "";

    public string Copyright { get; init; } = "";

    public string Language { get; init; } = "";

    public string MinClientVersion { get; init; } = "";

    public bool RequireLicenseAcceptance { get; init; }

    public required IReadOnlyList<string> PackageTypes { get; init; }

    public required IReadOnlyList<IndexedDependencyGroup> DependencyGroups { get; init; }

    public required long Size { get; init; }

    /// <summary>Base64 SHA-512 of the whole nupkg.</summary>
    public required string Sha512 { get; init; }

    public required byte[] Nuspec { get; init; }

    /// <summary>What the files say the package is, for a feed that is used for one kind only. Not stored.</summary>
    public PackageContent Content { get; init; } = new(PackageContentKind.Other, "");
}

/// <summary>A dependency group. <see cref="TargetFramework"/> is a short folder name, empty for "any".</summary>
public sealed record IndexedDependencyGroup(string TargetFramework, IReadOnlyList<IndexedDependency> Dependencies);

/// <summary>A dependency; <see cref="VersionRange"/> is the normalised range, empty for any version.</summary>
public sealed record IndexedDependency(string Id, string VersionRange);
