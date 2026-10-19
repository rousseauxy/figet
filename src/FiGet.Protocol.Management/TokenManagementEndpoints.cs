using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Protocol.Management;

/// <summary>
/// Service tokens over HTTP, under <c>/api/management/tokens</c>: what a script needs to give a host or a pipeline its
/// own key and take it away again, without a person on the tokens page. FiGet's own; nothing of the server being
/// replaced is mirrored here.
///
/// The caller is a service token with the admin scope, and it is treated as an administrator, not a super administrator:
/// it makes and revokes keys that read, publish and delete, and never one with the admin scope. A key that can make
/// admin keys is a key that can make more of itself; that stays with a person on a page.
/// </summary>
public static class TokenManagementEndpoints
{
    /// <summary>The longest lifetime a request may ask for. A key meant to live longer is made without an expiry, on purpose.</summary>
    public const int MaxExpiresInDays = 3650;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Whoever asks is an administrator and no more: the ceiling rule then refuses the admin scope by itself.</summary>
    private static readonly AccountActor ApiActor = new(0, UserRole.Admin);

    public static IEndpointRouteBuilder MapTokenManagement(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/tokens");
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync).DisableAntiforgery();
        group.MapDelete("/{id:int}", RevokeAsync);
        return app;
    }

    /// <summary>What a request to make a token says. Every field but the name and the scopes may be left out.</summary>
    public sealed record TokenRequest(string? Name, IReadOnlyList<string>? Scopes, string? Feed, int? ExpiresInDays);

    private static async Task<IResult> ListAsync(HttpContext http, IAccessTokenStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var tokens = await store.ListAsync(cancellationToken);

        // Service tokens only. A personal key is its owner's, shown on their profile and to admins on the tokens page.
        return Results.Json(tokens.Where(t => t.UserKey is null).Select(t => Describe(t, now)).ToList(), Json);
    }

    private static async Task<IResult> CreateAsync(HttpContext http, AccessTokenService tokens, IFeedStore feeds, AuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        // Read here and not bound by the framework: a body that cannot be read is then refused after the caller was
        // checked, so a stranger learns "credentials are required" and nothing about what the body should look like.
        TokenRequest? request = null;
        if (http.Request.HasJsonContentType())
        {
            try
            {
                request = await http.Request.ReadFromJsonAsync<TokenRequest>(Json, cancellationToken);
            }
            catch (JsonException)
            {
                request = null;
            }
        }

        if (request is null)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object: name, scopes, and optionally feed and expiresInDays.");
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
        {
            return Refuse(StatusCodes.Status400BadRequest, "name is required, at most 128 characters.");
        }

        var scopes = TokenScopes.None;
        foreach (var scope in request.Scopes ?? [])
        {
            switch (scope?.Trim().ToLowerInvariant())
            {
                case "read":
                    scopes |= TokenScopes.Read;
                    break;
                case "push":
                    scopes |= TokenScopes.Push;
                    break;
                case "delete":
                    scopes |= TokenScopes.Delete;
                    break;
                case "admin":
                    return Refuse(StatusCodes.Status403Forbidden, "A token with the admin scope is made on the tokens page, by a super administrator.");
                default:
                    return Refuse(StatusCodes.Status400BadRequest, $"'{scope}' is not a scope. Use read, push and delete.");
            }
        }

        if (scopes == TokenScopes.None)
        {
            return Refuse(StatusCodes.Status400BadRequest, "scopes is required: one or more of read, push and delete.");
        }

        if (request.ExpiresInDays is < 1 or > MaxExpiresInDays)
        {
            return Refuse(StatusCodes.Status400BadRequest, $"expiresInDays must be from 1 to {MaxExpiresInDays}; leave it out for a token that does not expire.");
        }

        Feed? feed = null;
        if (!string.IsNullOrWhiteSpace(request.Feed))
        {
            feed = await feeds.FindAsync(request.Feed.Trim(), cancellationToken);
            if (feed is null)
            {
                return Refuse(StatusCodes.Status400BadRequest, $"There is no feed or asset directory named '{request.Feed.Trim()}'.");
            }
        }

        DateTime? expires = request.ExpiresInDays is { } days ? time.GetUtcNow().UtcDateTime.AddDays(days) : null;
        var result = await tokens.CreateServiceTokenAsync(ApiActor, request.Name, scopes, feed?.Key, expires, cancellationToken);
        if (result.Created is not { } created)
        {
            return result.Status == TokenCreateStatus.AboveYourRights
                ? Refuse(StatusCodes.Status403Forbidden, "That token would carry more than this one may give.")
                : Refuse(StatusCodes.Status400BadRequest, "name and scopes are required.");
        }

        // The same line the tokens page writes, so a key's origin reads the same whoever made it; who did is the actor.
        audit.Record(
            http,
            "token.create",
            created.Token.Name,
            $"scopes={scopes} feed={feed?.Name ?? "all"} expires={(expires is null ? "never" : expires.Value.ToString("u", CultureInfo.InvariantCulture))}");

        created.Token.Feed = feed;
        var body = Describe(created.Token, time.GetUtcNow().UtcDateTime) with { Token = created.Secret };
        return Results.Json(body, Json, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeAsync(HttpContext http, int id, IAccessTokenStore store, AccessTokenService tokens, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        // A personal key does not exist here, the same as in the list.
        if (await store.FindAsync(id, cancellationToken) is not { UserKey: null } token)
        {
            return Refuse(StatusCodes.Status404NotFound, "There is no service token with that id.");
        }

        if (token.Scopes.HasFlag(TokenScopes.Admin))
        {
            return Refuse(StatusCodes.Status403Forbidden, "A token with the admin scope is revoked on the tokens page.");
        }

        // Revoking what is already revoked is not an error: the caller wanted it gone, and it is.
        if (token.RevokedUtc is null && await tokens.RevokeAsync(id, cancellationToken))
        {
            audit.Record(http, "token.revoke", token.Name);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Lets a service token with the admin scope through; null then. Anything else gets its answer here: 403 for a key
    /// that is valid and may not do this, 401 for no key, and for a key that is not one the same answers a feed gives.
    /// </summary>
    private static Task<IResult?> AdminAsync(HttpContext http, CancellationToken cancellationToken) =>
        ManagementAccess.AdminAsync(http, "token management", "Managing tokens takes a service token with the admin scope.", cancellationToken);

    private static IResult Refuse(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static TokenJson Describe(AccessToken token, DateTime now) => new(
        token.Key,
        token.Name,
        token.Prefix,
        Names(token.Scopes),
        token.Feed?.Name,
        Utc(token.CreatedUtc),
        Utc(token.ExpiresUtc),
        Utc(token.LastUsedUtc),
        Utc(token.RevokedUtc),
        token.RevokedUtc is null && (token.ExpiresUtc is null || token.ExpiresUtc > now),
        TokenExpiry.DaysLeft(token, now));

    /// <summary>Times are stored as UTC and read back without saying so; said here, so the JSON carries its Z.</summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } set ? Utc(set) : null;

    private static List<string> Names(TokenScopes scopes)
    {
        var names = new List<string>(4);
        if (scopes.HasFlag(TokenScopes.Read))
        {
            names.Add("read");
        }

        if (scopes.HasFlag(TokenScopes.Push))
        {
            names.Add("push");
        }

        if (scopes.HasFlag(TokenScopes.Delete))
        {
            names.Add("delete");
        }

        if (scopes.HasFlag(TokenScopes.Admin))
        {
            names.Add("admin");
        }

        return names;
    }

    /// <summary>
    /// A service token as the API shows it. <see cref="Token"/> is the secret, present once: in the answer that made it.
    /// <see cref="Feed"/> is absent for a token that applies to every feed, and <see cref="DaysLeft"/> for one that does
    /// not end or no longer works: what a script reads to replace a key before it stops.
    /// </summary>
    private sealed record TokenJson(
        int Id,
        string Name,
        string Prefix,
        IReadOnlyList<string> Scopes,
        string? Feed,
        DateTime CreatedUtc,
        DateTime? ExpiresUtc,
        DateTime? LastUsedUtc,
        DateTime? RevokedUtc,
        bool Active,
        int? DaysLeft,
        string? Token = null);
}
