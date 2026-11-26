using System.Diagnostics;
using FiGet.Http;

namespace FiGet.Web.Logging;

/// <summary>
/// One structured line per request, so somebody can answer "did that client reach us, and what did it ask
/// for" without guessing from timings. Every request only when <c>FiGet:Logging:Requests</c> says so, which is
/// what the server being replaced does too - its HTTP request log is opt-in, and its per-download table is
/// a per-feed checkbox. The requests that went wrong - a server error, a client that gave up, a slow start - are
/// logged whatever the switch says.
///
/// It exists because the absence was felt: a colleague reported a package "no longer being cached", and
/// with nothing logged there was no way to tell whether his client had reached this server at all. The
/// answer had to be argued from download counters and how long his install took.
///
/// Both addresses are recorded on purpose. Forwarded headers are honoured only for proxies the runtime
/// trusts, and the default trusts loopback alone, so behind a reverse proxy on another address
/// <see cref="ConnectionInfo.RemoteIpAddress"/> is the proxy rather than the caller. Logging the raw
/// header beside it means the line is never quietly wrong about who asked.
/// </summary>
public sealed class RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> logger, IConfiguration configuration)
{
    /// <summary>Every request, at Information. Off by default: a package client is chatty.</summary>
    private readonly bool everything = configuration.GetValue<bool>("FiGet:Logging:Requests");

    /// <summary>
    /// How long a request may take to start answering before it is logged as slow, whatever the switch says. Measured to
    /// the first byte, so a large download that starts at once is not slow however long it streams. Zero turns it off.
    /// </summary>
    private readonly TimeSpan slow = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("FiGet:Logging:SlowRequestSeconds", 5d)));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsNoise(context.Request.Path))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        TimeSpan? firstByte = null;
        context.Response.OnStarting(() =>
        {
            firstByte = Stopwatch.GetElapsedTime(started);
            return Task.CompletedTask;
        });

        var threw = false;
        try
        {
            await next(context);
        }
        catch
        {
            threw = true;
            throw;
        }
        finally
        {
            // In a finally block: a request that failed is the one most worth having a line for.
            Write(context, Stopwatch.GetElapsedTime(started), firstByte, threw);
        }
    }

    /// <summary>
    /// One line. With the switch on, every request at Information. With it off, only the ones somebody will be hunting for,
    /// at Warning: an answer of 500 or more, a request the client gave up on, and one that took longer than
    /// <see cref="slow"/> to start answering. These were invisible during a load test on 2026-11-26: clients saw timeouts,
    /// cut-off answers and 404s, and the server's log had nothing to say about any of them.
    /// </summary>
    private void Write(HttpContext context, TimeSpan elapsed, TimeSpan? firstByte, bool threw)
    {
        // An unhandled exception is turned into a 500 by a handler outside this one, after this line is written; until then
        // the status still reads 200.
        var status = threw ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
        var aborted = context.RequestAborted.IsCancellationRequested;
        var waited = firstByte ?? elapsed;
        var outcome = aborted
            ? firstByte is null ? "client left before an answer" : "client left during the answer"
            : threw ? "failed" : "done";
        var problem = status >= 500 || aborted || (slow > TimeSpan.Zero && waited >= slow);
        if (!everything && !problem)
        {
            return;
        }

        logger.Log(
            problem ? LogLevel.Warning : LogLevel.Information,
            "{Method} {Path}{Query} -> {Status} {Outcome} in {ElapsedMs:F0}ms (first byte {FirstByteMs}) | caller={Caller} forwarded={Forwarded} who={Who} agent={Agent}",
            context.Request.Method,
            context.Request.Path.Value,
            Query(context.Request),
            status,
            outcome,
            elapsed.TotalMilliseconds,
            firstByte is { } fb ? fb.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "ms" : "none",
            context.Connection.RemoteIpAddress?.ToString() ?? "-",
            Header(context, "X-Forwarded-For"),
            RequestActor.Describe(context),
            Header(context, "User-Agent"));
    }

    /// <summary>
    /// The query string, except where it carries a provider's authorization code and state: the sign-in callback and what
    /// follows it. A code is single-use and bound to its sign-in, but logs travel further than the database does.
    /// </summary>
    private static string? Query(HttpRequest request) =>
        request.QueryString.HasValue
        && (request.Path.StartsWithSegments(SignIn.OidcSchemes.CallbackSegment, StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/account/external", StringComparison.OrdinalIgnoreCase))
            ? "?(query not logged)"
            : request.QueryString.Value;

    private static string Header(HttpContext context, string name)
    {
        var value = context.Request.Headers[name].ToString();
        return value.Length > 0 ? value : "-";
    }

    /// <summary>
    /// Browser furniture: stylesheets, scripts, icons and fonts, fetched again on every page and asked for
    /// by nobody. One session of somebody clicking around the site was 80 requests, 38 of them the same two
    /// stylesheets, which buries the handful of lines that say what a package client did.
    ///
    /// Health probes and the framework's own paths go the same way, for the same reason.
    /// </summary>
    private static bool IsNoise(PathString path)
    {
        // Protocol traffic is never noise, whatever it is named. A package may legitimately be called
        // something.css, an asset directory is full of installers named setup.js and logo.png, and these
        // are the requests the log exists for - so this test comes first and the extension check below can
        // never swallow a download.
        if (path.StartsWithSegments("/nuget", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/endpoints", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/themes", StringComparison.OrdinalIgnoreCase)
            || IsAsset(path.Value);
    }

    /// <summary>
    /// Named by extension rather than by folder, because the static assets are served from the web root
    /// with a content hash in the name - <c>/app.9eycm9ixdl.css</c> - and there is no prefix to match on.
    /// Deliberately short: <c>.json</c>, <c>.xml</c>, <c>.nupkg</c> and <c>.nuspec</c> are not here,
    /// because those are answers to package clients.
    /// </summary>
    private static bool IsAsset(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var dot = path.LastIndexOf('.');
        return dot >= 0 && AssetExtensions.Contains(path[dot..], StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] AssetExtensions =
    [
        ".css", ".js", ".mjs", ".map", ".ico", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
    ];
}
