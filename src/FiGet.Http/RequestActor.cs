using Microsoft.AspNetCore.Http;

namespace FiGet.Http;

/// <summary>
/// Who a request turned out to be. One implementation, because the request log and the audit log both
/// answer this question and two copies of it would drift - and an audit line naming the wrong person is
/// worse than no audit line at all.
/// </summary>
public static class RequestActor
{
    /// <summary>
    /// The token that was accepted, or the signed-in user, or nobody. A feed with anonymous read answers
    /// without either, and that is worth seeing as "anonymous" rather than as a blank.
    /// </summary>
    public static string Describe(HttpContext? context)
    {
        if (context is null)
        {
            return "system";
        }

        if (context.Items.TryGetValue(FeedAccess.TokenNameItem, out var token) && token is string name && name.Length > 0)
        {
            return "token:" + name;
        }

        var user = context.User.Identity;
        return user?.IsAuthenticated == true && !string.IsNullOrEmpty(user.Name) ? "user:" + user.Name : "anonymous";
    }

    /// <summary>
    /// Who published, for a package version's "published by": the accepted token's name as <see cref="Describe"/> gives it
    /// but without its prefix, or the signed-in account's user name. Null when neither: nobody to name.
    /// </summary>
    public static string? Publisher(HttpContext? context)
    {
        if (context?.Items.TryGetValue(FeedAccess.TokenNameItem, out var token) == true && token is string name && name.Length > 0)
        {
            return name;
        }

        var user = context?.User.Identity;
        return user?.IsAuthenticated == true && !string.IsNullOrEmpty(user.Name) ? user.Name : null;
    }

    /// <summary>
    /// The address the request came from: the connection's, which the forwarded-headers middleware replaces with the one a
    /// trusted proxy saw. Never the <c>X-Forwarded-For</c> header itself - the client writes it, and behind a proxy that
    /// appends, what is left of it after the middleware took the proxy's entry is exactly the part the client wrote. The
    /// rate limiter keys on the same address.
    /// </summary>
    public static string Caller(HttpContext? context) =>
        context?.Connection.RemoteIpAddress?.ToString() ?? "-";
}
