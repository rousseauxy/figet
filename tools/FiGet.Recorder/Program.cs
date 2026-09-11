// FiGet.Recorder: a recording reverse proxy for build plan phase 0.
//
// Point a client at the recorder instead of the reference server. Every exchange is forwarded unchanged and
// written to disk: request line, headers (credentials redacted), bodies (text kept, binaries reduced to a
// SHA-256 and a length), status and response headers. The client's Host header is passed through, because
// NuGet servers build absolute URLs from it; otherwise the client would follow those URLs straight to the
// reference server and the rest of the conversation would go unrecorded.
//
//   dotnet run --project tools/FiGet.Recorder -- --upstream http://reference-server --listen http://127.0.0.1:5590 --out recordings/session1
//
// Label what follows with: POST /_recorder/scenario?name=find-module-by-name
// Raw recordings stay out of git (recordings/ is ignored); only scrubbed fixtures are committed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateSlimBuilder(args);
var upstream = new Uri(builder.Configuration["upstream"] ?? throw new InvalidOperationException("--upstream is required"));
var listen = builder.Configuration["listen"] ?? "http://127.0.0.1:5590";
var output = Path.GetFullPath(builder.Configuration["out"] ?? Path.Combine("recordings", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
Directory.CreateDirectory(output);

builder.WebHost.UseUrls(listen);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
var client = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
})
{
    Timeout = TimeSpan.FromMinutes(10),
};

var sequence = 0;
var scenario = "unlabelled";
var gate = new object();
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var redactedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Authorization", "X-NuGet-ApiKey", "X-ApiKey", "Cookie", "Set-Cookie", "Proxy-Authorization" };
var hopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer" };

app.MapPost("/_recorder/scenario", (string name) =>
{
    lock (gate)
    {
        scenario = Regex.Replace(name, "[^A-Za-z0-9._-]", "-");
    }

    Console.WriteLine($"--- scenario: {scenario}");
    return Results.Ok(new { scenario });
});

// A catch-all route, not app.Run (a terminal middleware runs before the control endpoint above) and not
// MapFallback (it skips paths with a file extension, such as index.json and every .nupkg).
app.Map("/{**path}", async (HttpContext context) =>
{
    var request = context.Request;
    var started = DateTime.UtcNow;

    using var requestBody = new MemoryStream();
    await request.Body.CopyToAsync(requestBody, context.RequestAborted);

    var target = new Uri(upstream, request.PathBase + request.Path + request.QueryString);
    using var forward = new HttpRequestMessage(new HttpMethod(request.Method), target);
    if (requestBody.Length > 0)
    {
        forward.Content = new ByteArrayContent(requestBody.ToArray());
    }

    foreach (var header in request.Headers)
    {
        if (hopByHop.Contains(header.Key) || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!forward.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            forward.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    // Keep the client's view of the server, so absolute URLs in responses point back at the recorder.
    forward.Headers.Host = request.Host.Value;

    HttpResponseMessage response;
    try
    {
        response = await client.SendAsync(forward, HttpCompletionOption.ResponseContentRead, context.RequestAborted);
    }
    catch (HttpRequestException ex)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync("Recorder could not reach the upstream: " + ex.Message);
        return;
    }

    using (response)
    {
        var responseBody = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (!hopByHop.Contains(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        context.Response.Headers.Remove("Content-Length");
        context.Response.ContentLength = responseBody.Length;
        await context.Response.Body.WriteAsync(responseBody, context.RequestAborted);

        int number;
        string label;
        lock (gate)
        {
            number = ++sequence;
            label = scenario;
        }

        var record = new
        {
            sequence = number,
            scenario = label,
            startedUtc = started,
            elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            request = new
            {
                method = request.Method,
                pathAndQuery = request.Path + request.QueryString,
                pathAndQueryDecoded = WebUtility.UrlDecode(request.Path + request.QueryString.Value),
                headers = Headers(request.Headers.Select(h => (h.Key, h.Value.ToString()))),
                body = Body(requestBody.ToArray(), request.ContentType),
            },
            response = new
            {
                status = (int)response.StatusCode,
                reason = response.ReasonPhrase,
                headers = Headers(response.Headers.Concat(response.Content.Headers).Select(h => (h.Key, string.Join(", ", h.Value)))),
                body = Body(responseBody, response.Content.Headers.ContentType?.ToString()),
            },
        };

        var file = $"{number:D5}-{label}-{request.Method}-{Slug(request.Path.Value)}.json";
        await File.WriteAllTextAsync(Path.Combine(output, file), JsonSerializer.Serialize(record, json), CancellationToken.None);
        await File.AppendAllTextAsync(
            Path.Combine(output, "index.tsv"),
            string.Join('\t', number, label, request.Method, (int)response.StatusCode, record.elapsedMs, WebUtility.UrlDecode(request.Path + request.QueryString.Value)) + "\n",
            CancellationToken.None);
        Console.WriteLine($"{number,5} {(int)response.StatusCode} {request.Method,-6} {WebUtility.UrlDecode(request.Path + request.QueryString.Value)}");
    }
});

Console.WriteLine($"Recording {listen} -> {upstream} into {output}");
await app.RunAsync();

Dictionary<string, string> Headers(IEnumerable<(string Key, string Value)> headers) =>
    headers
        .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => redactedHeaders.Contains(g.Key) ? "(redacted)" : string.Join(", ", g.Select(h => h.Value)), StringComparer.OrdinalIgnoreCase);

object? Body(byte[] content, string? contentType)
{
    if (content.Length == 0)
    {
        return null;
    }

    var textual = contentType is not null
        && (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
    if (textual && content.Length <= 4 * 1024 * 1024)
    {
        return new { length = content.Length, text = Encoding.UTF8.GetString(content) };
    }

    return new { length = content.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(content)), contentType };
}

static string Slug(string? path)
{
    var slug = Regex.Replace(path ?? "", "[^A-Za-z0-9]+", "_").Trim('_');
    return slug.Length > 80 ? slug[..80] : slug.Length == 0 ? "root" : slug;
}
