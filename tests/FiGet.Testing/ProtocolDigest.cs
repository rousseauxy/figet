using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FiGet.Testing;

/// <summary>
/// The structural digest of a protocol response: what a compatible server must reproduce, without the body
/// itself. Shared by the tool that reduces recordings to fixtures and by the tests that replay them, so a
/// recorded answer and FiGet's answer are always reduced by the same code - a digest that drifted between the
/// two would make every comparison meaningless.
/// </summary>
public static class ProtocolDigest
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace App = "http://www.w3.org/2007/app";
    private static readonly XNamespace D = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace M = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    /// <summary>
    /// Digests one response body. <paramref name="text"/> is null for a body that is not text; a response with
    /// no body at all is <c>empty</c>.
    /// </summary>
    public static JsonNode Response(bool hasBody, string? text, long length, string? contentType)
    {
        if (!hasBody)
        {
            return new JsonObject { ["kind"] = "empty" };
        }

        if (text is null)
        {
            return new JsonObject { ["kind"] = "binary", ["length"] = length };
        }

        var media = MediaType(contentType) ?? "";
        if (media.Contains("xml", StringComparison.OrdinalIgnoreCase) || media.Contains("atom", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Xml(XDocument.Parse(text));
            }
            catch (System.Xml.XmlException)
            {
                return new JsonObject { ["kind"] = "invalid-xml", ["length"] = length };
            }
        }

        if (media.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var node = JsonNode.Parse(text);
                return node is JsonObject obj
                    ? new JsonObject { ["kind"] = "json-object", ["properties"] = new JsonArray(obj.Select(p => (JsonNode)p.Key).OrderBy(k => (string)k!, StringComparer.Ordinal).ToArray<JsonNode?>()) }
                    : new JsonObject { ["kind"] = "json", ["length"] = length };
            }
            catch (JsonException)
            {
                return new JsonObject { ["kind"] = "invalid-json", ["length"] = length };
            }
        }

        return new JsonObject { ["kind"] = media.Length == 0 ? "text" : media, ["length"] = length };
    }

    /// <summary>The media type without parameters, lower-cased.</summary>
    public static string? MediaType(string? contentType) =>
        contentType?.Split(';')[0].Trim().ToLowerInvariant();

    /// <summary>An absolute or relative content URL reduced to its path, with the feed name replaced by <c>{feed}</c>.</summary>
    public static string? ContentPath(string? src) =>
        src is null ? null : Regex.Replace(Uri.TryCreate(src, UriKind.Absolute, out var uri) ? uri.AbsolutePath : src, "^/nuget/[^/]+", "/nuget/{feed}", RegexOptions.CultureInvariant);

    private static JsonNode Xml(XDocument document)
    {
        var root = document.Root!;
        if (root.Name == App + "service")
        {
            return new JsonObject
            {
                ["kind"] = "service-document",
                ["collections"] = new JsonArray(root.Descendants(App + "collection").Select(c => (JsonNode)(string)c.Attribute("href")!).ToArray<JsonNode?>()),
            };
        }

        var entries = root.Name == Atom + "entry" ? [root] : root.Elements(Atom + "entry").ToList();
        var digest = new JsonObject
        {
            ["kind"] = root.Name == Atom + "entry" ? "atom-entry" : root.Name == Atom + "feed" ? "atom-feed" : "xml:" + root.Name.LocalName,
        };

        if (root.Name == Atom + "feed")
        {
            digest["entryCount"] = entries.Count;
            digest["count"] = (string?)root.Element(M + "count");
            digest["nextLink"] = root.Elements(Atom + "link").Any(l => (string?)l.Attribute("rel") == "next");
        }

        var properties = entries
            .SelectMany(e => e.Descendants(M + "properties").Elements())
            .Select(p => p.Name.LocalName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(n => (JsonNode)n)
            .ToArray<JsonNode?>();
        if (properties.Length > 0)
        {
            digest["properties"] = new JsonArray(properties);
        }

        if (entries.Count > 0)
        {
            digest["entries"] = new JsonArray(entries.Select(e =>
            {
                var props = e.Descendants(M + "properties").FirstOrDefault();
                string? Prop(string name) => (string?)props?.Element(D + name);
                return (JsonNode)new JsonObject
                {
                    ["id"] = Prop("Id") ?? (string?)e.Element(Atom + "title"),
                    ["version"] = Prop("Version"),
                    ["normalizedVersion"] = Prop("NormalizedVersion"),
                    ["isLatestVersion"] = Prop("IsLatestVersion"),
                    ["isAbsoluteLatestVersion"] = Prop("IsAbsoluteLatestVersion"),
                    ["isPrerelease"] = Prop("IsPrerelease"),
                    ["listed"] = Prop("Listed"),
                    ["hasDependencies"] = !string.IsNullOrEmpty(Prop("Dependencies")),
                    ["contentSrc"] = ContentPath((string?)e.Element(Atom + "content")?.Attribute("src")),
                };
            }).ToArray<JsonNode?>());
        }

        return digest;
    }
}
