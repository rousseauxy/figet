namespace FiGet.Domain.Entities;

/// <summary>One version of a package, with the metadata read from its nuspec.</summary>
public sealed class PackageVersion
{
    public long Key { get; set; }

    public long PackageKey { get; set; }

    public Package? Package { get; set; }

    /// <summary>The version exactly as written in the nuspec, for example <c>1.2.3.0</c>.</summary>
    public required string OriginalVersion { get; set; }

    /// <summary>NuGet-normalised version, for example <c>1.2.3</c>, in original casing.</summary>
    public required string NormalizedVersion { get; set; }

    /// <summary>Lower-cased normalised version: the lookup key and the flat container path segment.</summary>
    public required string NormalizedVersionLower { get; set; }

    public bool IsPrerelease { get; set; }

    /// <summary>True when the version or any dependency range needs SemVer 2.0.0 to be understood.</summary>
    public bool IsSemVer2 { get; set; }

    public bool Listed { get; set; } = true;

    public PackageOrigin Origin { get; set; } = PackageOrigin.Pushed;

    public string Authors { get; set; } = "";

    public string Description { get; set; } = "";

    public string Summary { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Tags exactly as published (space-separated). PowerShellGet metadata lives here.</summary>
    public string Tags { get; set; } = "";

    public string IconUrl { get; set; } = "";

    public string LicenseUrl { get; set; } = "";

    public string LicenseExpression { get; set; } = "";

    public string ProjectUrl { get; set; } = "";

    public string RepositoryUrl { get; set; } = "";

    public string RepositoryType { get; set; } = "";

    public string ReleaseNotes { get; set; } = "";

    public string Copyright { get; set; } = "";

    public string Language { get; set; } = "";

    public string MinClientVersion { get; set; } = "";

    public bool RequireLicenseAcceptance { get; set; }

    /// <summary>Package types as <c>|Name|Name|</c>, original casing. Defaults to <c>|Dependency|</c>.</summary>
    public string PackageTypes { get; set; } = "|Dependency|";

    public DateTime PublishedUtc { get; set; }

    public DateTime LastUpdatedUtc { get; set; }

    public long Size { get; set; }

    /// <summary>Base64 SHA-512 of the nupkg.</summary>
    public string Hash { get; set; } = "";

    public string HashAlgorithm { get; set; } = "SHA512";

    public long Downloads { get; set; }

    /// <summary>Lower-cased id, title, tags, summary, description and authors, for provider-neutral search.</summary>
    public string SearchTextLower { get; set; } = "";

    /// <summary>Lower-cased tags padded as <c> tag1 tag2 </c>, so a token match is a plain contains.</summary>
    public string TagsLower { get; set; } = "";

    /// <summary>Lower-cased <see cref="PackageTypes"/>.</summary>
    public string PackageTypesLower { get; set; } = "|dependency|";

    public List<PackageDependency> Dependencies { get; set; } = [];
}

public enum PackageOrigin
{
    Pushed,
    Cached,
}
