using System.Text.Json.Serialization;

namespace FiGet.Protocol.V3;

// Shapes follow nuget.org. Every "@type" is mandatory: the PackageManagement NuGet provider reads them
// through its dynamic JSON parser and fails with a NullReferenceException when one is missing.

public sealed record ServiceIndex(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("resources")] IReadOnlyList<ServiceResource> Resources,
    [property: JsonPropertyName("@context")] ServiceIndexContext Context);

public sealed record ServiceResource(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type,
    [property: JsonPropertyName("comment")] string Comment);

public sealed record ServiceIndexContext(
    [property: JsonPropertyName("@vocab")] string Vocab,
    [property: JsonPropertyName("comment")] string Comment);

public sealed record RegistrationIndex(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] IReadOnlyList<string> Type,
    [property: JsonPropertyName("commitId")] string CommitId,
    [property: JsonPropertyName("commitTimeStamp")] string CommitTimeStamp,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] IReadOnlyList<RegistrationPage> Items,
    [property: JsonPropertyName("@context")] RegistrationContext Context);

public sealed record RegistrationPage(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type,
    [property: JsonPropertyName("commitId")] string CommitId,
    [property: JsonPropertyName("commitTimeStamp")] string CommitTimeStamp,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RegistrationLeafItem>? Items,
    [property: JsonPropertyName("parent")] string Parent,
    [property: JsonPropertyName("lower")] string Lower,
    [property: JsonPropertyName("upper")] string Upper);

/// <summary>A registration page served as its own document, when the index does not inline pages.</summary>
public sealed record RegistrationPageDocument(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type,
    [property: JsonPropertyName("commitId")] string CommitId,
    [property: JsonPropertyName("commitTimeStamp")] string CommitTimeStamp,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] IReadOnlyList<RegistrationLeafItem> Items,
    [property: JsonPropertyName("parent")] string Parent,
    [property: JsonPropertyName("lower")] string Lower,
    [property: JsonPropertyName("upper")] string Upper,
    [property: JsonPropertyName("@context")] RegistrationContext Context);

public sealed record RegistrationLeafItem(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type,
    [property: JsonPropertyName("commitId")] string CommitId,
    [property: JsonPropertyName("commitTimeStamp")] string CommitTimeStamp,
    [property: JsonPropertyName("catalogEntry")] CatalogEntry CatalogEntry,
    [property: JsonPropertyName("packageContent")] string PackageContent,
    [property: JsonPropertyName("registration")] string Registration);

public sealed record CatalogEntry
{
    [JsonPropertyName("@id")]
    public required string Id { get; init; }

    [JsonPropertyName("@type")]
    public string Type { get; init; } = "PackageDetails";

    [JsonPropertyName("authors")]
    public required string Authors { get; init; }

    [JsonPropertyName("dependencyGroups")]
    public required IReadOnlyList<DependencyGroup> DependencyGroups { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("iconUrl")]
    public required string IconUrl { get; init; }

    [JsonPropertyName("id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("language")]
    public required string Language { get; init; }

    [JsonPropertyName("licenseExpression")]
    public required string LicenseExpression { get; init; }

    [JsonPropertyName("licenseUrl")]
    public required string LicenseUrl { get; init; }

    [JsonPropertyName("listed")]
    public required bool Listed { get; init; }

    [JsonPropertyName("minClientVersion")]
    public required string MinClientVersion { get; init; }

    [JsonPropertyName("packageContent")]
    public required string PackageContent { get; init; }

    [JsonPropertyName("projectUrl")]
    public required string ProjectUrl { get; init; }

    [JsonPropertyName("published")]
    public required string Published { get; init; }

    [JsonPropertyName("requireLicenseAcceptance")]
    public required bool RequireLicenseAcceptance { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyList<string> Tags { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

public sealed record DependencyGroup(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type,
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<Dependency> Dependencies);

public sealed record Dependency(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type,
    [property: JsonPropertyName("id")] string PackageId,
    [property: JsonPropertyName("range")] string Range,
    [property: JsonPropertyName("registration")] string Registration);

public sealed record RegistrationContext(
    [property: JsonPropertyName("@vocab")] string Vocab,
    [property: JsonPropertyName("catalog")] string Catalog,
    [property: JsonPropertyName("xsd")] string Xsd);

public sealed record RegistrationLeaf(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] IReadOnlyList<string> Type,
    [property: JsonPropertyName("catalogEntry")] string CatalogEntry,
    [property: JsonPropertyName("listed")] bool Listed,
    [property: JsonPropertyName("packageContent")] string PackageContent,
    [property: JsonPropertyName("published")] string Published,
    [property: JsonPropertyName("registration")] string Registration,
    [property: JsonPropertyName("@context")] RegistrationContext Context);

public sealed record FlatContainerVersions(
    [property: JsonPropertyName("versions")] IReadOnlyList<string> Versions);

public sealed record SearchResponse(
    [property: JsonPropertyName("@context")] SearchContext Context,
    [property: JsonPropertyName("totalHits")] int TotalHits,
    [property: JsonPropertyName("data")] IReadOnlyList<SearchResult> Data);

public sealed record SearchContext(
    [property: JsonPropertyName("@vocab")] string Vocab,
    [property: JsonPropertyName("@base")] string Base);

public sealed record SearchResult
{
    [JsonPropertyName("@id")]
    public required string Id { get; init; }

    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Package";

    [JsonPropertyName("registration")]
    public required string Registration { get; init; }

    [JsonPropertyName("id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("iconUrl")]
    public required string IconUrl { get; init; }

    [JsonPropertyName("licenseUrl")]
    public required string LicenseUrl { get; init; }

    [JsonPropertyName("projectUrl")]
    public required string ProjectUrl { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyList<string> Tags { get; init; }

    [JsonPropertyName("authors")]
    public required IReadOnlyList<string> Authors { get; init; }

    [JsonPropertyName("owners")]
    public IReadOnlyList<string> Owners { get; init; } = [];

    [JsonPropertyName("totalDownloads")]
    public required long TotalDownloads { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("packageTypes")]
    public required IReadOnlyList<SearchPackageType> PackageTypes { get; init; }

    [JsonPropertyName("versions")]
    public required IReadOnlyList<SearchVersion> Versions { get; init; }
}

public sealed record SearchPackageType([property: JsonPropertyName("name")] string Name);

public sealed record SearchVersion(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("downloads")] long Downloads,
    [property: JsonPropertyName("@id")] string Id);

public sealed record AutocompleteResponse(
    [property: JsonPropertyName("@context")] SearchContext Context,
    [property: JsonPropertyName("totalHits")] int TotalHits,
    [property: JsonPropertyName("data")] IReadOnlyList<string> Data);
