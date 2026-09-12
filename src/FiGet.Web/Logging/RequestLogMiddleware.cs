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
                Who(context),
                Header(context, "User-Agent"));
        }
    }

    /// <summary>
    /// Who this request turned out to be: the token that was accepted, or the signed-in user, or nobody.
    /// A feed with anonymous read answers without either, and that is worth seeing as "anonymous" rather
    /// than as a blank.
    /// </summary>
    private static string Who(HttpContext context)
    {
        if (context.Items.TryGetValue(FeedAccess.TokenNameItem, out var token) && token is string name && name.Length > 0)
        {
            return "token:" + name;
        }

        var user = context.User.Identity;
        return user?.IsAuthenticated == true && !string.IsNullOrEmpty(user.Name) ? "user:" + user.Name : "anonymous";
    }

    private static string Header(HttpContext context, string name)
    {
        var value = context.Request.Headers[name].ToString();
        return value.Length > 0 ? value : "-";
    }

    /// <summary>
    /// Health probes and the framework's own assets, which a container polls constantly and nobody is ever
    /// looking for. Everything a package client does is kept.
    /// </summary>
    private static bool IsNoise(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase);
}
