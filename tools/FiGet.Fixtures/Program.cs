// FiGet.Fixtures: turns raw FiGet.Recorder output into fixtures that are safe to commit.
//
//   dotnet run --project tools/FiGet.Fixtures -- --in recordings/<run> --out tests/fixtures/<client> --client "<client versions>" --reference "<server version>"
//
// A fixture keeps the facts a compatible server must reproduce and drops everything else:
//   request   method, path with the feed name replaced by {feed}, query string, selected headers, whether
//             credentials were sent (never their values), and the kind and size of the body;
//   response  status, content type, and a structural digest of the body: for Atom feeds the property names
//             and, per entry, id, version and the latest/prerelease/listed flags; for the service document its
//             collections; for JSON the top-level property names; for anything else only the length.
// Response bodies are never copied verbatim, so no reference server's output ends up in the repository.
// The run fails when the output contains anything that looks like an address, a user path or a secret.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var options = ParseArguments(args);
var input = Path.GetFullPath(options.GetValueOrDefault("in") ?? throw new ArgumentException("--in is required"));
var output = Path.GetFullPath(options.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
var client = options.GetValueOrDefault("client") ?? throw new ArgumentException("--client is required");
var reference = options.GetValueOrDefault("reference") ?? throw new ArgumentException("--reference is required");

var keptRequestHeaders = new[] { "Accept", "Content-Type", "User-Agent", "X-NuGet-Protocol-Version" };
var credentialHeaders = new[] { "Authorization", "X-NuGet-ApiKey", "X-ApiKey" };
var ignoredScenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "start", "cleanup", "unlabelled" };
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
XNamespace atom = "http://www.w3.org/2005/Atom";
XNamespace app = "http://www.w3.org/2007/app";
XNamespace d = "http://schemas.microsoft.com/ado/2007/08/dataservices";
XNamespace m = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

var records = Directory.GetFiles(input, "*.json")
    .Select(f => JsonNode.Parse(File.ReadAllText(f))!)
    .OrderBy(r => (int)r["sequence"]!)
    .Where(r => !ignoredScenarios.Contains((string)r["scenario"]!))
    .ToList();

Directory.CreateDirectory(output);
var written = 0;
foreach (var scenario in records.GroupBy(r => (string)r["scenario"]!))
{
    var fixture = new JsonObject
    {
        ["client"] = client,
        ["reference"] = reference,
        ["scenario"] = scenario.Key,
        ["exchanges"] = new JsonArray(scenario.Select(Digest).ToArray<JsonNode?>()),
    };

    var text = fixture.ToJsonString(json);
    AssertClean(text, scenario.Key);
    await File.WriteAllTextAsync(Path.Combine(output, scenario.Key + ".json"), text + "\n");
    written++;
}

Console.WriteLine($"{written} fixtures from {records.Count} exchanges written to {output}");
return;

JsonNode Digest(JsonNode record)
{
    var request = record["request"]!;
    var response = record["response"]!;
    var pathAndQuery = (string)request["pathAndQueryDecoded"]!;
    var queryIndex = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
    var path = queryIndex < 0 ? pathAndQuery : pathAndQuery[..queryIndex];
    var query = queryIndex < 0 ? "" : pathAndQuery[(queryIndex + 1)..];
    path = Regex.Replace(path, "^/nuget/[^/]+", "/nuget/{feed}", RegexOptions.CultureInvariant);

    var requestHeaders = (JsonObject)request["headers"]!;
    var headers = new JsonObject();
    foreach (var name in keptRequestHeaders)
    {
        var value = Header(requestHeaders, name);
        if (value is not null)
        {
            headers[name] = Regex.Replace(value, "boundary=\"?[^;\"]+\"?", "boundary={boundary}", RegexOptions.CultureInvariant);
        }
    }

    var responseHeaders = (JsonObject)response["headers"]!;
    return new JsonObject
    {
        ["request"] = new JsonObject
        {
            ["method"] = (string)request["method"]!,
            ["path"] = path,
            ["query"] = query,
            ["headers"] = headers,
            ["credentials"] = new JsonArray(credentialHeaders.Where(h => Header(requestHeaders, h) is not null).Select(h => (JsonNode)h).ToArray()),
            ["body"] = BodyKind(request["body"], Header(requestHeaders, "Content-Type")),
        },
        ["response"] = new JsonObject
        {
            ["status"] = (int)response["status"]!,
            ["contentType"] = MediaType(Header(responseHeaders, "Content-Type")),
            ["body"] = ResponseDigest(response["body"], Header(responseHeaders, "Content-Type")),
        },
    };
}

JsonNode? BodyKind(JsonNode? body, string? contentType)
{
    if (body is null)
    {
        return null;
    }

    var length = (long)body["length"]!;
    var kind = contentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true ? "multipart-package" : "bytes";
    return new JsonObject { ["kind"] = kind, ["length"] = length };
}

JsonNode? ResponseDigest(JsonNode? body, string? contentType)
{
    if (body is null)
    {
        return new JsonObject { ["kind"] = "empty" };
    }

    var length = (long)body["length"]!;
    var text = (string?)body["text"];
    if (text is null)
    {
        return new JsonObject { ["kind"] = "binary", ["length"] = length };
    }

    var media = MediaType(contentType) ?? "";
    if (media.Contains("xml", StringComparison.OrdinalIgnoreCase) || media.Contains("atom", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            return XmlDigest(XDocument.Parse(text));
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

JsonNode XmlDigest(XDocument document)
{
    var root = document.Root!;
    if (root.Name == app + "service")
    {
        return new JsonObject
        {
            ["kind"] = "service-document",
            ["collections"] = new JsonArray(root.Descendants(app + "collection").Select(c => (JsonNode)(string)c.Attribute("href")!).ToArray<JsonNode?>()),
        };
    }

    var entries = root.Name == atom + "entry" ? [root] : root.Elements(atom + "entry").ToList();
    var digest = new JsonObject
    {
        ["kind"] = root.Name == atom + "entry" ? "atom-entry" : root.Name == atom + "feed" ? "atom-feed" : "xml:" + root.Name.LocalName,
    };

    if (root.Name == atom + "feed")
    {
        digest["entryCount"] = entries.Count;
        digest["count"] = (string?)root.Element(m + "count");
        digest["nextLink"] = root.Elements(atom + "link").Any(l => (string?)l.Attribute("rel") == "next");
    }

    var properties = entries
        .SelectMany(e => e.Descendants(m + "properties").Elements())
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
            var props = e.Descendants(m + "properties").FirstOrDefault();
            string? Prop(string name) => (string?)props?.Element(d + name);
            var entry = new JsonObject
            {
                ["id"] = Prop("Id") ?? (string?)e.Element(atom + "title"),
                ["version"] = Prop("Version"),
                ["normalizedVersion"] = Prop("NormalizedVersion"),
                ["isLatestVersion"] = Prop("IsLatestVersion"),
                ["isAbsoluteLatestVersion"] = Prop("IsAbsoluteLatestVersion"),
                ["isPrerelease"] = Prop("IsPrerelease"),
                ["listed"] = Prop("Listed"),
                ["hasDependencies"] = !string.IsNullOrEmpty(Prop("Dependencies")),
                ["contentSrc"] = ContentPath((string?)e.Element(atom + "content")?.Attribute("src")),
            };
            return (JsonNode)entry;
        }).ToArray<JsonNode?>());
    }

    return digest;
}

static string? ContentPath(string? src) =>
    src is null ? null : Regex.Replace(Uri.TryCreate(src, UriKind.Absolute, out var uri) ? uri.AbsolutePath : src, "^/nuget/[^/]+", "/nuget/{feed}", RegexOptions.CultureInvariant);

static string? Header(JsonObject headers, string name) =>
    headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();

static string? MediaType(string? contentType) =>
    contentType?.Split(';')[0].Trim().ToLowerInvariant();

static void AssertClean(string text, string scenario)
{
    var forbidden = new (string Name, string Pattern)[]
    {
        // Four-part numbers are everywhere as versions (user agents, PowerShell modules), so only private
        // ranges and addresses inside URLs count as leaks.
        ("private IPv4 address", @"\b(192\.168|172\.(1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b"),
        ("IPv4 address in a URL", @"(//|@)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}"),
        ("host name with port", @"[A-Za-z0-9.-]+:\d{2,5}/"),
        ("Windows user path", @"[A-Za-z]:\\\\Users\\\\|/Users/"),
        ("secret-like hex token", @"\b[0-9a-f]{40}\b"),
        ("figet token", @"figet_[A-Za-z0-9_-]{20,}"),
    };

    foreach (var (name, pattern) in forbidden)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        if (match.Success)
        {
            throw new InvalidOperationException($"Fixture '{scenario}' contains a {name}: '{match.Value}'. Nothing was written for it.");
        }
    }
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (arguments[i].StartsWith("--", StringComparison.Ordinal))
        {
            result[arguments[i][2..]] = arguments[i + 1];
            i++;
        }
    }

    return result;
}
