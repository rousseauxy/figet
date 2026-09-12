using System.Net.Http.Headers;
using System.Text;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Http;

/// <summary>A feed resolved for a request, with the token that was presented (if any and valid).</summary>
public sealed record FeedRequest(Feed Feed, ValidatedToken? Token);

/// <summary>
/// Resolves the feed named in the route and decides whether the request may perform an operation on it.
/// Every protocol endpoint starts here, so the access rules exist once.
/// </summary>
public static class FeedAccess
{
    public const string Realm = "FiGet";

    /// <summary>
    /// Where the accepted token's name is left for the request log to pick up. Set here because this is
    /// the one place every protocol request resolves a feed and a token, so attribution exists once.
    /// </summary>
    public const string TokenNameItem = "figet:token-name";

    /// <summary>
    /// Returns the feed and token, or an <see cref="IResult"/> to send instead:
    /// 404 for an unknown feed, 401 with a Basic challenge when credentials are missing or invalid,
    /// 403 when a valid token lacks the scope.
    /// </summary>
    public static async Task<(FeedRequest? Request, IResult? Error)> ResolveAsync(HttpContext http, string feedName, TokenScopes required, CancellationToken cancellationToken)
    {
        var feeds = http.RequestServices.GetRequiredService<IFeedStore>();
        var feed = await feeds.FindAsync(feedName, cancellationToken);
        if (feed is null)
        {
            return (null, Results.NotFound(new { error = $"Feed '{feedName}' does not exist." }));
        }

        var tokens = http.RequestServices.GetRequiredService<AccessTokenService>();
        ValidatedToken? best = null;
        foreach (var secret in RequestCredentials.Candidates(http.Request))
        {
            var token = await tokens.ValidateAsync(secret, cancellationToken);
            if (token is null)
            {
                continue;
            }

            best ??= token;
            if (token.Allows(required, feed.Key))
            {
                best = token;
                break;
            }
        }

        var allowed = required == TokenScopes.Read && feed.AnonymousRead
            || (best is not null && best.Allows(required, feed.Key));
        if (allowed)
        {
            if (best is not null)
            {
                http.Items[TokenNameItem] = best.Name;
            }

            return (new FeedRequest(feed, best), null);
        }

        if (best is not null)
        {
            return (null, Results.Json(new { error = "The token does not grant this operation on this feed." }, statusCode: StatusCodes.Status403Forbidden));
        }

        // Pushing with a key that does not validate is a 403 on nuget.org; everything else asks for credentials.
        if (required != TokenScopes.Read && RequestCredentials.HasApiKeyHeader(http.Request))
        {
            return (null, Results.Json(new { error = "The API key is invalid, expired or revoked." }, statusCode: StatusCodes.Status403Forbidden));
        }

        http.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\"";
        return (null, Results.Json(new { error = "Credentials are required." }, statusCode: StatusCodes.Status401Unauthorized));
    }
}

/// <summary>Reads token secrets from every header the supported clients use.</summary>
public static class RequestCredentials
{
    private static readonly string[] ApiKeyHeaders = ["X-NuGet-ApiKey", "X-ApiKey"];

    public static bool HasApiKeyHeader(HttpRequest request) =>
        ApiKeyHeaders.Any(h => !string.IsNullOrWhiteSpace(request.Headers[h]));

    /// <summary>
    /// Candidate secrets in order: API key headers, the password of Basic auth (the user name is ignored,
    /// as NuGet clients put anything there), then a Bearer token.
    /// </summary>
    public static IEnumerable<string> Candidates(HttpRequest request)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in ApiKeyHeaders)
        {
            var value = request.Headers[header].ToString().Trim();
            if (value.Length > 0 && seen.Add(value))
            {
                yield return value;
            }
        }

        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var authorization) || string.IsNullOrEmpty(authorization.Parameter))
        {
            yield break;
        }

        if (authorization.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter));
            }
            catch (FormatException)
            {
                yield break;
            }

            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            var password = colon >= 0 ? decoded[(colon + 1)..] : decoded;
            if (password.Length > 0 && seen.Add(password))
            {
                yield return password;
            }
        }
        else if (authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && seen.Add(authorization.Parameter))
        {
            yield return authorization.Parameter;
        }
    }
}
