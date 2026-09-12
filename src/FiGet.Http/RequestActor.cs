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

    /// <summary>The address the request came from, preferring what a trusted proxy forwarded.</summary>
    public static string Caller(HttpContext? context)
    {
        if (context is null)
        {
            return "-";
        }

        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        return forwarded.Length > 0 ? forwarded : context.Connection.RemoteIpAddress?.ToString() ?? "-";
    }
}
