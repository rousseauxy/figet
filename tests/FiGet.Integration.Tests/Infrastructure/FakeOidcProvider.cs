using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FiGet.Integration.Tests.Infrastructure;

/// <summary>Who the fake provider signs in as next: the person at the provider's login screen.</summary>
public sealed record FakeIdentity(string Subject, string? UserName = null, string? Email = null, string? Name = null, string[]? Groups = null, bool? EmailVerified = null);

/// <summary>
/// A minimal OpenID Connect provider on a loopback port: discovery, keys, authorize, token and user info, with the checks
/// that matter to a client - redirect URI, client secret, PKCE, nonce - so a sign-in through it is a real code flow with a
/// signed ID token, not a shortcut around FiGet's handler. The login screen is skipped: <see cref="Next"/> is who signs in.
/// </summary>
public sealed class FakeOidcProvider : IAsyncDisposable
{
    public const string ClientId = "figet-test";
    public const string ClientSecret = "fake-client-secret-0123456789";

    private readonly WebApplication app;
    private readonly RsaSecurityKey signingKey;
    private readonly ConcurrentDictionary<string, Grant> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakeIdentity> accessTokens = new(StringComparer.Ordinal);
    private int tokenRequests;

    private FakeOidcProvider(WebApplication app, RsaSecurityKey signingKey)
    {
        this.app = app;
        this.signingKey = signingKey;
    }

    /// <summary>The issuer, without a trailing slash.</summary>
    public string Authority { get; private set; } = "";

    /// <summary>Who signs in at the next authorize request. Null answers <c>access_denied</c>, as a person cancelling would.</summary>
    public FakeIdentity? Next { get; set; }

    public int TokenRequests => tokenRequests;

    public static async Task<FakeOidcProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var rsa = RSA.Create(2048);
        var provider = new FakeOidcProvider(app, new RsaSecurityKey(rsa) { KeyId = "fake-key" });
        provider.Map();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        provider.Authority = address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/');
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private void Map()
    {
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Authority,
            ["authorization_endpoint"] = Authority + "/authorize",
            ["token_endpoint"] = Authority + "/token",
            ["userinfo_endpoint"] = Authority + "/userinfo",
            ["jwks_uri"] = Authority + "/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
        }));

        app.MapGet("/jwks", () =>
        {
            var parameters = signingKey.Rsa.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", alg = "RS256", kid = signingKey.KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) },
                },
            });
        });

        app.MapGet("/authorize", (HttpRequest request) =>
        {
            var query = request.Query;
            var redirect = query["redirect_uri"].ToString();
            var state = Uri.EscapeDataString(query["state"].ToString());
            if (query["client_id"] != ClientId || string.IsNullOrEmpty(redirect))
            {
                return Results.BadRequest("unknown client");
            }

            if (Next is not { } identity)
            {
                return Results.Redirect($"{redirect}?error=access_denied&state={state}");
            }

            var code = Guid.NewGuid().ToString("N");
            codes[code] = new Grant(identity, query["nonce"].ToString(), redirect, query["code_challenge"].ToString());
            return Results.Redirect($"{redirect}?code={code}&state={state}");
        });

        app.MapPost("/token", async (HttpRequest request) =>
        {
            Interlocked.Increment(ref tokenRequests);
            var form = await request.ReadFormAsync();
            var (clientId, secret) = ClientCredentials(request, form);
            if (clientId != ClientId || secret != ClientSecret)
            {
                return Results.Json(new { error = "invalid_client" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!codes.TryRemove(form["code"].ToString(), out var grant)
                || grant.RedirectUri != form["redirect_uri"].ToString()
                || grant.CodeChallenge != Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))))
            {
                return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var accessToken = Guid.NewGuid().ToString("N");
            accessTokens[accessToken] = grant.Identity;
            var claims = Claims(grant.Identity);
            claims["nonce"] = grant.Nonce;
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Authority,
                Audience = ClientId,
                IssuedAt = DateTime.UtcNow,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddMinutes(5),
                Claims = claims,
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            });
            return Results.Json(new { access_token = accessToken, token_type = "Bearer", expires_in = 300, id_token = idToken });
        });

        app.MapGet("/userinfo", (HttpRequest request) =>
        {
            var header = request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.Ordinal) && accessTokens.TryGetValue(header[7..], out var identity)
                ? Results.Json(Claims(identity))
                : Results.StatusCode(StatusCodes.Status401Unauthorized);
        });
    }

    private static Dictionary<string, object> Claims(FakeIdentity identity)
    {
        var claims = new Dictionary<string, object> { ["sub"] = identity.Subject };
        if (identity.UserName is not null)
        {
            claims["preferred_username"] = identity.UserName;
        }

        if (identity.Email is not null)
        {
            claims["email"] = identity.Email;
        }

        if (identity.Name is not null)
        {
            claims["name"] = identity.Name;
        }

        if (identity.EmailVerified is { } verified)
        {
            claims["email_verified"] = verified;
        }

        if (identity.Groups is not null)
        {
            claims["groups"] = identity.Groups;
        }

        return claims;
    }

    private static (string? ClientId, string? Secret) ClientCredentials(HttpRequest request, IFormCollection form)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            return (Uri.UnescapeDataString(decoded[..colon]), Uri.UnescapeDataString(decoded[(colon + 1)..]));
        }

        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    private sealed record Grant(FakeIdentity Identity, string Nonce, string RedirectUri, string CodeChallenge);
}
