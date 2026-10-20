using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FiGet.Application.Tokens;
using FiGet.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Protocol.Management;

/// <summary>
/// Who may call the management routes under <c>/api/management</c>: a service token with the admin scope, treated as an
/// administrator. One check for every group there, so tokens and feeds cannot drift apart in who they let in.
/// </summary>
internal static class ManagementAccess
{
    /// <summary>How every management route reads and writes JSON: web casing, nulls left out, text as it is.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The body as the record, or null when it is not JSON of that shape. Read by the route and not bound by the framework,
    /// so a body that cannot be read is refused after the caller was checked: a stranger learns nothing about it.
    /// </summary>
    public static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken cancellationToken)
        where T : class
    {
        if (!http.Request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await http.Request.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when the caller may go on; otherwise the answer to send instead.</summary>
    /// <param name="area">What the audit line of a refused key names, for example "token management".</param>
    /// <param name="forbidden">What a valid key without the admin scope is told.</param>
    public static async Task<IResult?> AdminAsync(HttpContext http, string area, string forbidden, CancellationToken cancellationToken)
    {
        var tokens = http.RequestServices.GetRequiredService<AccessTokenService>();
        ValidatedToken? valid = null;
        string? refused = null;
        foreach (var secret in RequestCredentials.Candidates(http.Request))
        {
            if (await tokens.ValidateAsync(secret, cancellationToken) is not { } token)
            {
                refused ??= secret;
                continue;
            }

            valid ??= token;

            // A key of FiGet's own that belongs to nobody. A personal key never carries the admin scope, and an access token
            // from a trusted issuer reads and publishes at most.
            if (token is { IsAdmin: true, Owner: null, External: null })
            {
                http.Items[FeedAccess.TokenNameItem] = token.LogName;
                return null;
            }
        }

        if (valid is not null)
        {
            http.Items[FeedAccess.TokenNameItem] = valid.LogName;
            return Refuse(StatusCodes.Status403Forbidden, forbidden);
        }

        // No valid key: an anonymous request, whatever it sent, and counted as one.
        if (http.RequestServices.GetService<RequestRateLimits>() is { } limits && !limits.TryAcquire(http, RequestRateLimits.Anonymous))
        {
            return Results.Text("Too many requests from this address without credentials. Try again shortly.", "text/plain", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (refused is not null && (await tokens.DescribeRefusedAsync(refused, cancellationToken) is var known) && (known is not null || RequestCredentials.HasApiKeyHeader(http.Request)))
        {
            var subject = known?.Name ?? "unknown key";
            http.RequestServices.GetRequiredService<AuditLog>().RecordThrottled(
                http,
                $"token.refused|{subject}|{RequestActor.Caller(http)}|management",
                TimeSpan.FromMinutes(10),
                "token.refused",
                subject,
                $"feed=({area}) reason={known?.Reason ?? "not a key"}");
        }

        if (RequestCredentials.HasApiKeyHeader(http.Request))
        {
            return Refuse(StatusCodes.Status403Forbidden, "The API key or access token is invalid, expired or revoked.");
        }

        http.Response.Headers.WWWAuthenticate = $"Basic realm=\"{FeedAccess.Realm}\"";
        return Refuse(StatusCodes.Status401Unauthorized, "Credentials are required.");
    }

    public static IResult Refuse(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
