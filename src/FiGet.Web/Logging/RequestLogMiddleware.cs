using System.Diagnostics;
using FiGet.Http;

namespace FiGet.Web.Logging;

/// <summary>
/// One structured line per request, so somebody can answer "did that client reach us, and what did it ask
/// for" without guessing from timings. Off unless <c>FiGet:Logging:Requests</c> says otherwise, which is
/// what the server being replaced does too - its HTTP request log is opt-in, and its per-download table is
/// a per-feed checkbox.
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
public sealed class RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsNoise(context.Request.Path))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            // In a finally block: a request that failed is the one most worth having a line for.
            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.LogInformation(
                "{Method} {Path}{Query} -> {Status} in {ElapsedMs:F0}ms | caller={Caller} forwarded={Forwarded} who={Who} agent={Agent}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Request.QueryString.Value,
                context.Response.StatusCode,
                elapsed.TotalMilliseconds,
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                Header(context, "X-Forwarded-For"),
                RequestActor.Describe(context),
                Header(context, "User-Agent"));
        }
    }

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
