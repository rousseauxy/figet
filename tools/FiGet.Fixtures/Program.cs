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

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FiGet.Testing;

var options = ParseArguments(args);
var input = Path.GetFullPath(options.GetValueOrDefault("in") ?? throw new ArgumentException("--in is required"));
var output = Path.GetFullPath(options.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
var client = options.GetValueOrDefault("client") ?? throw new ArgumentException("--client is required");
var reference = options.GetValueOrDefault("reference") ?? throw new ArgumentException("--reference is required");

var keptRequestHeaders = new[] { "Accept", "Content-Type", "User-Agent", "X-NuGet-Protocol-Version" };
var credentialHeaders = new[] { "Authorization", "X-NuGet-ApiKey", "X-ApiKey" };
var ignoredScenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "start", "cleanup", "unlabelled" };
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
            ["contentType"] = ProtocolDigest.MediaType(Header(responseHeaders, "Content-Type")),
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

JsonNode? ResponseDigest(JsonNode? body, string? contentType) =>
    body is null
        ? ProtocolDigest.Response(hasBody: false, text: null, length: 0, contentType)
        : ProtocolDigest.Response(hasBody: true, (string?)body["text"], (long)body["length"]!, contentType);

static string? Header(JsonObject headers, string name) =>
    headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();

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
