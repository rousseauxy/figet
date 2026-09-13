using System.Net.Http.Headers;
using System.Text;
using FiGet.Application.Accounts;
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
    public static Task<(FeedRequest? Request, IResult? Error)> ResolveAsync(HttpContext http, string feedName, TokenScopes required, CancellationToken cancellationToken) =>
        ResolveAsync(http, feedName, required, assets: false, cancellationToken);

    /// <summary>
    /// The same rules for an asset directory. A package feed named here does not exist, and an asset
    /// directory does not exist to the package endpoints: each surface sees only its own kind, so a NuGet
    /// client pointed at a directory gets a clean 404 rather than an empty feed that looks broken.
    /// </summary>
    public static Task<(FeedRequest? Request, IResult? Error)> ResolveAssetsAsync(HttpContext http, string directoryName, TokenScopes required, CancellationToken cancellationToken) =>
        ResolveAsync(http, directoryName, required, assets: true, cancellationToken);

    private static async Task<(FeedRequest? Request, IResult? Error)> ResolveAsync(HttpContext http, string feedName, TokenScopes required, bool assets, CancellationToken cancellationToken)
    {
        var feeds = http.RequestServices.GetRequiredService<IFeedStore>();
        var feed = await feeds.FindAsync(feedName, cancellationToken);
        if (feed is null || (feed.Kind == FeedKind.Assets) != assets)
        {
            return (null, Results.NotFound(new { error = assets ? $"Asset directory '{feedName}' does not exist." : $"Feed '{feedName}' does not exist." }));
        }

        var tokens = http.RequestServices.GetRequiredService<AccessTokenService>();
        ValidatedToken? best = null;
        var tokenAllows = false;
        string? refused = null;
        foreach (var secret in RequestCredentials.Candidates(http.Request))
        {
            var token = await tokens.ValidateAsync(secret, cancellationToken);
            if (token is null)
            {
                refused ??= secret;
                continue;
            }

            best ??= token;

            // For a personal key this also asks what its owner may do on the feed now.
            if (await tokens.AllowsAsync(token, required, feed, cancellationToken))
            {
                best = token;
                tokenAllows = true;
                break;
            }
        }

        if (best is null && refused is not null)
        {
            await RecordRefusedAsync(http, tokens, refused, feed, cancellationToken);
        }

        // Counted only now that the key is checked: a request without a valid key and without a sign-in is anonymous, whatever
        // headers it sent. A garbage key does not buy a way around the limit.
        if (!tokenAllows && http.User.Identity?.IsAuthenticated != true
            && http.RequestServices.GetService<RequestRateLimits>() is { } limits
            && !limits.TryAcquire(http, RequestRateLimits.Anonymous))
        {
            return (null, Results.Text("Too many requests from this address without credentials. Try again shortly, or use an API key.", "text/plain", statusCode: StatusCodes.Status429TooManyRequests));
        }

        var allowed = tokenAllows || (required == TokenScopes.Read && feed.AnonymousRead);

        // A signed-in browser - a download link on a package page - reads with its account's level. Reading only: a
        // cookie is sent with any request the browser makes, so it must never be what lets a push or a delete through.
        if (!allowed && required == TokenScopes.Read && best is null && AccountClaims.Actor(http.User) is { } actor)
        {
            var access = http.RequestServices.GetRequiredService<FeedAccessService>();
            allowed = await access.LevelAsync(feed, actor, cancellationToken) >= FeedAccessLevel.Read;
        }
        if (allowed)
        {
            if (best is not null)
            {
                http.Items[TokenNameItem] = best.LogName;
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

    /// <summary>
    /// A key that no longer works, still being sent: a scheduled job nobody updated, or a leaked key being tried. Once per
    /// ten minutes per key and address, because such a client repeats it on every request. A secret that is no token at
    /// all is recorded only when it came as an API key header; as a Basic password it may be someone's real password, and
    /// a NuGet client sends whatever is stored for the source.
    /// </summary>
    private static async Task RecordRefusedAsync(HttpContext http, AccessTokenService tokens, string secret, Feed feed, CancellationToken cancellationToken)
    {
        var known = await tokens.DescribeRefusedAsync(secret, cancellationToken);
        if (known is null && !RequestCredentials.HasApiKeyHeader(http.Request))
        {
            return;
        }

        var subject = known?.Name ?? "unknown key";
        var caller = RequestActor.Caller(http);
        http.RequestServices.GetRequiredService<AuditLog>().RecordThrottled(
            http,
            $"token.refused|{subject}|{caller}|{feed.Key}",
            TimeSpan.FromMinutes(10),
            "token.refused",
            subject,
            $"feed={feed.Name} reason={known?.Reason ?? "not a key"}");
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
