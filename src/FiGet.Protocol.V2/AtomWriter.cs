using System.Globalization;
using System.Text;
using System.Xml;
using FiGet.Domain.Entities;

namespace FiGet.Protocol.V2;

/// <summary>
/// Writes the OData/Atom documents of build plan section 4.3. Every property is emitted, empty when
/// unknown, because the recorded clients read a fixed set and some of them fail on a missing element
/// rather than on an empty one.
/// </summary>
public static class AtomWriter
{
    public const string AtomNamespace = "http://www.w3.org/2005/Atom";
    public const string DataNamespace = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    public const string MetadataNamespace = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
    public const string AppNamespace = "http://www.w3.org/2007/app";

    /// <summary>Unlisted versions report this date, as nuget.org does, next to Listed being false.</summary>
    private static readonly DateTime UnlistedPublished = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly XmlWriterSettings Settings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(false),
    };

    /// <summary>The service document at the v2 root: one collection, named Packages.</summary>
    public static string ServiceDocument(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Write(writer =>
        {
            writer.WriteStartElement("service", AppNamespace);
            writer.WriteAttributeString("base", "http://www.w3.org/XML/1998/namespace", root + "/");
            writer.WriteAttributeString("xmlns", "atom", null, AtomNamespace);
            writer.WriteStartElement("workspace", AppNamespace);
            writer.WriteStartElement("title", AtomNamespace);
            writer.WriteAttributeString("type", "text");
            writer.WriteString("Default");
            writer.WriteEndElement();
            writer.WriteStartElement("collection", AppNamespace);
            writer.WriteAttributeString("href", "Packages");
            writer.WriteStartElement("title", AtomNamespace);
            writer.WriteAttributeString("type", "text");
            writer.WriteString("Packages");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    /// <summary>A feed of entries, with the optional total count and next link OData paging uses.</summary>
    public static string Feed(IReadOnlyList<V2Row> rows, string feedRoot, string selfUrl, int? totalCount, string? nextUrl)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(feedRoot);
        return Write(writer =>
        {
            writer.WriteStartElement("feed", AtomNamespace);
            writer.WriteAttributeString("base", "http://www.w3.org/XML/1998/namespace", feedRoot + "/");
            writer.WriteAttributeString("xmlns", "d", null, DataNamespace);
            writer.WriteAttributeString("xmlns", "m", null, MetadataNamespace);

            writer.WriteElementString("id", AtomNamespace, selfUrl);
            writer.WriteStartElement("title", AtomNamespace);
            writer.WriteAttributeString("type", "text");
            writer.WriteString("Packages");
            writer.WriteEndElement();
            writer.WriteElementString("updated", AtomNamespace, Date(DateTime.UtcNow));
            writer.WriteStartElement("link", AtomNamespace);
            writer.WriteAttributeString("rel", "self");
            writer.WriteAttributeString("href", selfUrl);
            writer.WriteEndElement();

            if (totalCount is not null)
            {
                writer.WriteElementString("count", MetadataNamespace, totalCount.Value.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var row in rows)
            {
                WriteEntry(writer, row, feedRoot, standalone: false);
            }

            if (!string.IsNullOrEmpty(nextUrl))
            {
                writer.WriteStartElement("link", AtomNamespace);
                writer.WriteAttributeString("rel", "next");
                writer.WriteAttributeString("href", nextUrl);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
    }

    /// <summary>A single entry document, as Packages(Id=,Version=) returns.</summary>
    public static string Entry(V2Row row, string feedRoot)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(feedRoot);
        return Write(writer => WriteEntry(writer, row, feedRoot, standalone: true));
    }

    /// <summary>
    /// The EDMX description of V2FeedPackage. Clients that fetch it only check that it parses, so it
    /// describes the properties this server actually emits and the three function imports.
    /// </summary>
    public static string Metadata() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx Version="1.0" xmlns:edmx="http://schemas.microsoft.com/ado/2007/06/edmx">
          <edmx:DataServices xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" m:DataServiceVersion="2.0" m:MaxDataServiceVersion="2.0">
            <Schema Namespace="NuGetGallery.OData" xmlns="http://schemas.microsoft.com/ado/2006/04/edm">
              <EntityType Name="V2FeedPackage" m:HasStream="true">
                <Key>
                  <PropertyRef Name="Id" />
                  <PropertyRef Name="Version" />
                </Key>
                <Property Name="Id" Type="Edm.String" Nullable="false" />
                <Property Name="Version" Type="Edm.String" Nullable="false" />
                <Property Name="NormalizedVersion" Type="Edm.String" Nullable="true" />
                <Property Name="Authors" Type="Edm.String" Nullable="true" />
                <Property Name="Copyright" Type="Edm.String" Nullable="true" />
                <Property Name="Created" Type="Edm.DateTime" Nullable="false" />
                <Property Name="Dependencies" Type="Edm.String" Nullable="true" />
                <Property Name="Description" Type="Edm.String" Nullable="true" />
                <Property Name="DownloadCount" Type="Edm.Int64" Nullable="false" />
                <Property Name="GalleryDetailsUrl" Type="Edm.String" Nullable="true" />
                <Property Name="IconUrl" Type="Edm.String" Nullable="true" />
                <Property Name="IsLatestVersion" Type="Edm.Boolean" Nullable="false" />
                <Property Name="IsAbsoluteLatestVersion" Type="Edm.Boolean" Nullable="false" />
                <Property Name="IsPrerelease" Type="Edm.Boolean" Nullable="false" />
                <Property Name="Language" Type="Edm.String" Nullable="true" />
                <Property Name="LastUpdated" Type="Edm.DateTime" Nullable="false" />
                <Property Name="Published" Type="Edm.DateTime" Nullable="false" />
                <Property Name="PackageHash" Type="Edm.String" Nullable="true" />
                <Property Name="PackageHashAlgorithm" Type="Edm.String" Nullable="true" />
                <Property Name="PackageSize" Type="Edm.Int64" Nullable="false" />
                <Property Name="ProjectUrl" Type="Edm.String" Nullable="true" />
                <Property Name="ReleaseNotes" Type="Edm.String" Nullable="true" />
                <Property Name="ReportAbuseUrl" Type="Edm.String" Nullable="true" />
                <Property Name="RequireLicenseAcceptance" Type="Edm.Boolean" Nullable="false" />
                <Property Name="Summary" Type="Edm.String" Nullable="true" />
                <Property Name="Tags" Type="Edm.String" Nullable="true" />
                <Property Name="Title" Type="Edm.String" Nullable="true" />
                <Property Name="VersionDownloadCount" Type="Edm.Int64" Nullable="false" />
                <Property Name="MinClientVersion" Type="Edm.String" Nullable="true" />
                <Property Name="LastEdited" Type="Edm.DateTime" Nullable="true" />
                <Property Name="LicenseUrl" Type="Edm.String" Nullable="true" />
                <Property Name="LicenseNames" Type="Edm.String" Nullable="true" />
                <Property Name="LicenseReportUrl" Type="Edm.String" Nullable="true" />
                <Property Name="Listed" Type="Edm.Boolean" Nullable="false" />
              </EntityType>
              <EntityContainer Name="V2FeedContext" m:IsDefaultEntityContainer="true">
                <EntitySet Name="Packages" EntityType="NuGetGallery.OData.V2FeedPackage" />
                <FunctionImport Name="Search" EntitySet="Packages" ReturnType="Collection(NuGetGallery.OData.V2FeedPackage)" m:HttpMethod="GET">
                  <Parameter Name="searchTerm" Type="Edm.String" Mode="In" />
                  <Parameter Name="targetFramework" Type="Edm.String" Mode="In" />
                  <Parameter Name="includePrerelease" Type="Edm.Boolean" Mode="In" />
                </FunctionImport>
                <FunctionImport Name="FindPackagesById" EntitySet="Packages" ReturnType="Collection(NuGetGallery.OData.V2FeedPackage)" m:HttpMethod="GET">
                  <Parameter Name="id" Type="Edm.String" Mode="In" />
                </FunctionImport>
                <FunctionImport Name="GetUpdates" EntitySet="Packages" ReturnType="Collection(NuGetGallery.OData.V2FeedPackage)" m:HttpMethod="GET">
                  <Parameter Name="packageIds" Type="Edm.String" Mode="In" />
                  <Parameter Name="versions" Type="Edm.String" Mode="In" />
                  <Parameter Name="includePrerelease" Type="Edm.Boolean" Mode="In" />
                  <Parameter Name="includeAllVersions" Type="Edm.Boolean" Mode="In" />
                  <Parameter Name="targetFrameworks" Type="Edm.String" Mode="In" />
                  <Parameter Name="versionConstraints" Type="Edm.String" Mode="In" />
                </FunctionImport>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    /// <summary>Dependencies as id:range:targetFramework triples joined by a pipe, the v2 convention.</summary>
    public static string Dependencies(IEnumerable<PackageDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        return string.Join(
            '|',
            dependencies
                .OrderBy(d => d.Ordinal)
                .Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Id}:{d.VersionRange}:{d.TargetFramework}")));
    }

    private static void WriteEntry(XmlWriter writer, V2Row row, string feedRoot, bool standalone)
    {
        var metadata = row.Metadata;
        var key = string.Create(CultureInfo.InvariantCulture, $"Packages(Id='{row.Id}',Version='{row.OriginalVersion}')");
        var self = feedRoot + "/" + key;

        writer.WriteStartElement("entry", AtomNamespace);
        if (standalone)
        {
            writer.WriteAttributeString("base", "http://www.w3.org/XML/1998/namespace", feedRoot + "/");
            writer.WriteAttributeString("xmlns", "d", null, DataNamespace);
            writer.WriteAttributeString("xmlns", "m", null, MetadataNamespace);
        }

        writer.WriteElementString("id", AtomNamespace, self);
        writer.WriteStartElement("category", AtomNamespace);
        writer.WriteAttributeString("term", "NuGetGallery.OData.V2FeedPackage");
        writer.WriteAttributeString("scheme", "http://schemas.microsoft.com/ado/2007/08/dataservices/scheme");
        writer.WriteEndElement();
        writer.WriteStartElement("link", AtomNamespace);
        writer.WriteAttributeString("rel", "edit");
        writer.WriteAttributeString("href", key);
        writer.WriteEndElement();
        writer.WriteStartElement("title", AtomNamespace);
        writer.WriteAttributeString("type", "text");
        writer.WriteString(row.Id);
        writer.WriteEndElement();
        writer.WriteElementString("updated", AtomNamespace, Date(metadata.LastUpdatedUtc));
        writer.WriteStartElement("author", AtomNamespace);
        writer.WriteElementString("name", AtomNamespace, metadata.Authors);
        writer.WriteEndElement();
        writer.WriteStartElement("content", AtomNamespace);
        writer.WriteAttributeString("type", "application/zip");
        writer.WriteAttributeString("src", Download(feedRoot, row));
        writer.WriteEndElement();

        writer.WriteStartElement("properties", MetadataNamespace);
        Text(writer, "Id", row.Id);
        Text(writer, "Version", row.OriginalVersion);
        Text(writer, "NormalizedVersion", row.NormalizedVersion);
        Text(writer, "Authors", metadata.Authors);
        Text(writer, "Copyright", metadata.Copyright);
        Typed(writer, "Created", "Edm.DateTime", Date(metadata.PublishedUtc));
        Text(writer, "Dependencies", Dependencies(metadata.Dependencies));
        Text(writer, "Description", metadata.Description);
        Typed(writer, "DownloadCount", "Edm.Int64", metadata.Downloads.ToString(CultureInfo.InvariantCulture));
        Text(writer, "GalleryDetailsUrl", self);
        Text(writer, "IconUrl", metadata.IconUrl);
        Typed(writer, "IsLatestVersion", "Edm.Boolean", Flag(row.Entry.IsLatestVersion));
        Typed(writer, "IsAbsoluteLatestVersion", "Edm.Boolean", Flag(row.Entry.IsAbsoluteLatestVersion));
        Typed(writer, "IsPrerelease", "Edm.Boolean", Flag(row.Version.IsPrerelease));
        Text(writer, "Language", metadata.Language);
        Typed(writer, "LastUpdated", "Edm.DateTime", Date(metadata.LastUpdatedUtc));
        Typed(writer, "Published", "Edm.DateTime", Date(row.Listed ? metadata.PublishedUtc : UnlistedPublished));
        Text(writer, "PackageHash", metadata.Hash);
        Text(writer, "PackageHashAlgorithm", metadata.HashAlgorithm);
        Typed(writer, "PackageSize", "Edm.Int64", metadata.Size.ToString(CultureInfo.InvariantCulture));
        Text(writer, "ProjectUrl", metadata.ProjectUrl);
        Text(writer, "ReleaseNotes", metadata.ReleaseNotes);
        Text(writer, "ReportAbuseUrl", "");
        Typed(writer, "RequireLicenseAcceptance", "Edm.Boolean", Flag(metadata.RequireLicenseAcceptance));
        Text(writer, "Summary", metadata.Summary);
        Text(writer, "Tags", metadata.Tags);
        Text(writer, "Title", metadata.Title);
        Typed(writer, "VersionDownloadCount", "Edm.Int64", metadata.Downloads.ToString(CultureInfo.InvariantCulture));
        Text(writer, "MinClientVersion", metadata.MinClientVersion);
        Null(writer, "LastEdited", "Edm.DateTime");
        Text(writer, "LicenseUrl", metadata.LicenseUrl);
        Text(writer, "LicenseNames", metadata.LicenseExpression);
        Text(writer, "LicenseReportUrl", "");
        Typed(writer, "Listed", "Edm.Boolean", Flag(row.Listed));
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    /// <summary>The download URL clients follow; the version segment is the normalised one.</summary>
    public static string Download(string feedRoot, V2Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{feedRoot}/package/{Uri.EscapeDataString(row.Id)}/{Uri.EscapeDataString(row.NormalizedVersion)}");
    }

    private static void Text(XmlWriter writer, string name, string? value) =>
        writer.WriteElementString(name, DataNamespace, value ?? "");

    private static void Typed(XmlWriter writer, string name, string type, string value)
    {
        writer.WriteStartElement(name, DataNamespace);
        writer.WriteAttributeString("type", MetadataNamespace, type);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void Null(XmlWriter writer, string name, string type)
    {
        writer.WriteStartElement(name, DataNamespace);
        writer.WriteAttributeString("type", MetadataNamespace, type);
        writer.WriteAttributeString("null", MetadataNamespace, "true");
        writer.WriteEndElement();
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static string Date(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Write(Action<XmlWriter> body)
    {
        var buffer = new StringBuilder();
        using (var writer = XmlWriter.Create(buffer, Settings))
        {
            body(writer);
            writer.Flush();
        }

        // XmlWriter writes an encoding of utf-16 for a StringBuilder target; the response is utf-8.
        return buffer.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", 0, Math.Min(60, buffer.Length)).ToString();
    }
}
