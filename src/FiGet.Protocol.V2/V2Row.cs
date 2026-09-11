using FiGet.Core.Entities;
using FiGet.Core.Versions;
using NuGet.Versioning;

namespace FiGet.Protocol.V2;

/// <summary>
/// One row of a v2 result: a package id and one entry of its merged version list. Filtering, ordering and
/// the Atom writer all read from this, so the merged list of build plan section 5 stays the only source of
/// the latest flags. A row always carries local metadata; upstream-only versions arrive in phase 3.
/// </summary>
public sealed record V2Row(string Id, VersionListEntry<PackageVersion> Entry)
{
    /// <summary>Versions with no local row are skipped while building, so this is never null.</summary>
    public PackageVersion Metadata => Entry.Payload!;

    public NuGetVersion Version => Entry.Version;

    /// <summary>The version as published, which may have four parts or leading zeroes.</summary>
    public string OriginalVersion => Metadata.OriginalVersion;

    public string NormalizedVersion => Metadata.NormalizedVersion;

    public bool Listed => Entry.Listed;

    /// <summary>Every row of one package, oldest version first, which is the order the reference server used.</summary>
    public static IReadOnlyList<V2Row> ForPackage(Package package, bool includeSemVer2)
    {
        ArgumentNullException.ThrowIfNull(package);
        return VersionListBuilder.BuildLocal(package.Versions, includeSemVer2)
            .Where(e => e.Payload is not null)
            .Select(e => new V2Row(package.Id, e))
            .ToList();
    }

    /// <summary>The value of an OData property, or null when the property has no value.</summary>
    /// <exception cref="ODataFilterException">The property is not part of the supported set.</exception>
    public object? Property(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.ToUpperInvariant() switch
        {
            "ID" => Id,
            "VERSION" => OriginalVersion,
            "NORMALIZEDVERSION" => NormalizedVersion,
            "TAGS" => Metadata.Tags,
            "TITLE" => Metadata.Title,
            "DESCRIPTION" => Metadata.Description,
            "SUMMARY" => Metadata.Summary,
            "AUTHORS" => Metadata.Authors,
            "ISLATESTVERSION" => Entry.IsLatestVersion,
            "ISABSOLUTELATESTVERSION" => Entry.IsAbsoluteLatestVersion,
            "ISPRERELEASE" => Version.IsPrerelease,
            "LISTED" => Listed,
            "PUBLISHED" => Metadata.PublishedUtc,
            "CREATED" => Metadata.PublishedUtc,
            "LASTUPDATED" => Metadata.LastUpdatedUtc,
            "DOWNLOADCOUNT" => Metadata.Downloads,
            "VERSIONDOWNLOADCOUNT" => Metadata.Downloads,
            _ => throw new ODataFilterException($"Unknown property '{name}'.", name),
        };
    }

    /// <summary>True for the properties compared as NuGet versions rather than as strings.</summary>
    public static bool IsVersionProperty(string name) =>
        name.Equals("Version", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NormalizedVersion", StringComparison.OrdinalIgnoreCase);
}
