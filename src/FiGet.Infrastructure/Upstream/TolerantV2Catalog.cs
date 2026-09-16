using System.Globalization;
using System.Xml.Linq;
using FiGet.Application.Ports;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace FiGet.Infrastructure.Upstream;

/// <summary>
/// A v2 gallery's <c>FindPackagesById()</c> read entry by entry, for when NuGet's own parser gives up on the whole answer.
/// Its parser throws on the first version it cannot parse, and with it every valid version of the id was lost - one odd
/// entry on a gallery made the package unavailable through FiGet. This skips such an entry and keeps the rest. It goes
/// through the repository's own HTTP source, so credentials, proxy and timeouts are the same as for every other call.
/// </summary>
internal static class TolerantV2Catalog
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    /// <summary>A gallery pages at 100; a package with thousands of versions is tens of pages, and this bounds a feed that never ends.</summary>
    private const int MaxPages = 200;

    /// <summary>
    /// One version's release notes, from the single-entry route every v2 gallery serves. Read here rather than through
    /// NuGet's own parser because that parser does not carry release notes at all - it drops the element - and the
    /// whole point of fetching one entry is the field it drops.
    ///
    /// Null when the gallery does not answer, or says nothing: a report shows no notes rather than a guess.
    /// </summary>
    public static async Task<string?> ReadReleaseNotesAsync(
        SourceRepository repository,
        string sourceUrl,
        string idLower,
        NuGetVersion version,
        CancellationToken cancellationToken)
    {
        var http = (await repository.GetResourceAsync<HttpSourceResource>(cancellationToken))?.HttpSource;
        if (http is null)
        {
            return null;
        }

        var root = sourceUrl.TrimEnd('/') + "/";
        var url = new Uri($"{root}Packages(Id='{Uri.EscapeDataString(idLower)}',Version='{Uri.EscapeDataString(version.ToNormalizedString())}')");
        var document = await http.ProcessStreamAsync(
            new HttpSourceRequest(url, NullLogger.Instance),
            async stream => stream is null ? null : await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken),
            NullLogger.Instance,
            cancellationToken);

        // The route answers one entry, but a gallery that answers a feed with one entry in it is just as correct.
        var entry = document?.Root is { } root2 && root2.Name == Atom + "entry"
            ? root2
            : document?.Root?.Elements(Atom + "entry").FirstOrDefault();
        var notes = entry?.Element(Meta + "properties")?.Element(Data + "ReleaseNotes")?.Value?.Trim();
        return string.IsNullOrEmpty(notes) ? null : notes;
    }

    public static async Task<IReadOnlyList<UpstreamMetadata>> ReadAsync(SourceRepository repository, string sourceUrl, string idLower, CancellationToken cancellationToken)
    {
        var http = (await repository.GetResourceAsync<HttpSourceResource>(cancellationToken))?.HttpSource
            ?? throw new InvalidOperationException("The upstream has no HTTP source to read its v2 feed through.");
        var root = sourceUrl.TrimEnd('/') + "/";
        var next = new Uri($"{root}FindPackagesById()?id='{Uri.EscapeDataString(idLower)}'&semVerLevel=2.0.0");
        var found = new List<UpstreamMetadata>();

        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            var document = await http.ProcessStreamAsync(
                new HttpSourceRequest(next, NullLogger.Instance),
                async stream => stream is null ? null : await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken),
                NullLogger.Instance,
                cancellationToken);
            if (document?.Root is not { } feed)
            {
                break;
            }

            foreach (var entry in feed.Elements(Atom + "entry"))
            {
                if (Read(entry) is { } metadata)
                {
                    found.Add(metadata);
                }
            }

            var href = feed.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == "next")?.Attribute("href")?.Value;
            next = href is null ? null : new Uri(new Uri(root), href);
        }

        return found;
    }

    /// <summary>One entry, or null when its version does not parse.</summary>
    private static UpstreamMetadata? Read(XElement entry)
    {
        var properties = entry.Element(Meta + "properties");
        string Text(string name) => properties?.Element(Data + name)?.Value ?? "";

        if (!NuGetVersion.TryParse(Text("Version"), out var version))
        {
            return null;
        }

        DateTime? published = DateTime.TryParse(Text("Published"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : null;
        var listed = bool.TryParse(Text("Listed"), out var flag) ? flag : published is not { Year: <= 1900 };
        return new UpstreamMetadata(
            version,
            Text("Description"),
            Text("Summary"),
            Text("Title"),
            Text("Authors"),
            Text("Tags").Trim(),
            Text("ProjectUrl"),
            Text("IconUrl"),
            Text("LicenseUrl"),
            published,
            long.TryParse(Text("DownloadCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var downloads) ? downloads : 0,
            listed,
            Dependencies(Text("Dependencies")));
    }

    /// <summary>
    /// The v2 dependency string, <c>id:range:framework|…</c>, in the shape <c>NuGetUpstreamClient</c> produces from NuGet's own
    /// parser: framework as a short folder name, the range normalised, a group without ids as one empty row.
    /// </summary>
    private static IReadOnlyList<UpstreamDependency> Dependencies(string text)
    {
        var groups = new Dictionary<string, List<UpstreamDependency>>(StringComparer.Ordinal);
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = part.Split(':');
            var id = fields.Length > 0 ? fields[0].Trim() : "";
            var range = fields.Length > 1 && VersionRange.TryParse(fields[1], out var parsed) && !parsed.Equals(VersionRange.All) ? parsed.ToNormalizedString() : "";
            var framework = fields.Length > 2 && fields[2].Length > 0 ? NuGetFramework.Parse(fields[2]) : NuGetFramework.AnyFramework;
            var folder = framework.IsAny || framework.IsUnsupported ? "" : framework.GetShortFolderName();
            if (!groups.TryGetValue(folder, out var members))
            {
                groups[folder] = members = [];
            }

            if (id.Length > 0)
            {
                members.Add(new UpstreamDependency(folder, id, range));
            }
        }

        return [.. groups.SelectMany(g => g.Value.Count > 0 ? g.Value : [new UpstreamDependency(g.Key, null, "")])];
    }
}
