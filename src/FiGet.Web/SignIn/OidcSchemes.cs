using System.Collections.Concurrent;
using System.Net;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using FiGet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace FiGet.Web.SignIn;

/// <summary>
/// OpenID Connect providers as authentication schemes that come from the database rather than from startup code. A scheme
/// named <c>oidc-{slug}</c> exists while an enabled provider has that slug, and its options are rebuilt when the
/// provider's row changes. Nothing is registered in memory ahead of time, so a provider saved on one replica is there on
/// every replica at its next sign-in, with no restart.
/// </summary>
public static class OidcSchemes
{
    public const string Prefix = "oidc-";

    /// <summary>Where a provider sends the browser back: <c>/signin-oidc/{slug}</c>.</summary>
    public const string CallbackSegment = "/signin-oidc";

    /// <summary>
    /// Holds the provider's answer between its callback and <c>/account/external/complete</c>, for a few minutes. Never a
    /// session: the account decision is made on the complete endpoint, which then issues the normal sign-in cookie.
    /// </summary>
    public const string ExternalCookie = "figet.external";

    public static string Name(string slug) => Prefix + slug.ToLowerInvariant();

    public static string CallbackPath(string slug) => $"{CallbackSegment}/{slug.ToLowerInvariant()}";

    public static void AddOidcProviders(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddCookie(ExternalCookie, cookie =>
            {
                cookie.Cookie.Name = ExternalCookie;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                cookie.SlidingExpiration = false;
            });
        services.AddWebEncoders();
        services.AddSingleton<IAuthenticationSchemeProvider, OidcSchemeProvider>();
        services.AddSingleton<IOptionsMonitor<OpenIdConnectOptions>, OidcOptionsMonitor>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
    }

    /// <summary>
    /// Runs a provider's callback. The authentication middleware only calls the handlers of schemes registered at
    /// startup, which these are not, so the callback path is recognised here and handed to that provider's handler.
    /// </summary>
    public static IApplicationBuilder UseOidcCallbacks(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(CallbackSegment, StringComparison.OrdinalIgnoreCase, out var rest)
                && rest.Value is { Length: > 1 } slug
                && slug.IndexOf('/', 1) < 0)
            {
                var handlers = context.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>();
                if (await handlers.GetHandlerAsync(context, Name(slug[1..])) is IAuthenticationRequestHandler handler
                    && await handler.HandleRequestAsync())
                {
                    return;
                }
            }

            await next(context);
        });

    internal static OidcProvider? FindEnabled(IServiceScopeFactory scopes, string schemeName)
    {
        if (!schemeName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slug = schemeName[Prefix.Length..];
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<FiGetDbContext>().OidcProviders.AsNoTracking().FirstOrDefault(p => p.Slug == slug && p.Enabled);
    }
}

/// <summary>The registered schemes, plus one per enabled provider, looked up by name when a request asks for it.</summary>
public sealed class OidcSchemeProvider(IOptions<AuthenticationOptions> options, IServiceScopeFactory scopes) : AuthenticationSchemeProvider(options)
{
    public override async Task<AuthenticationScheme?> GetSchemeAsync(string name)
    {
        if (await base.GetSchemeAsync(name) is { } registered)
        {
            return registered;
        }

        var provider = OidcSchemes.FindEnabled(scopes, name);
        return provider is null ? null : new AuthenticationScheme(name, provider.DisplayName, typeof(OpenIdConnectHandler));
    }
}

/// <summary>
/// Options for the provider schemes, built from the provider's row. Kept per scheme while the row's
/// <see cref="OidcProvider.UpdatedUtc"/> is unchanged, because the options hold the provider's discovery document and
/// signing keys and fetching those on every sign-in would be slow; a saved change moves the stamp and rebuilds them.
/// </summary>
public sealed class OidcOptionsMonitor(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    ISecretProtector secrets,
    IOptions<PublicUrlOptions> urls,
    TimeProvider time) : IOptionsMonitor<OpenIdConnectOptions>
{
    private readonly ConcurrentDictionary<string, (DateTime Version, OpenIdConnectOptions Options)> built = new(StringComparer.Ordinal);

    public OpenIdConnectOptions CurrentValue => Get(Options.DefaultName);

    public OpenIdConnectOptions Get(string? name)
    {
        var provider = name is null ? null : OidcSchemes.FindEnabled(scopes, name);
        if (provider is null)
        {
            // Asked for a scheme that no longer exists: options that cannot reach anything, rather than an exception in
            // the middle of the framework's handler set-up.
            return new OpenIdConnectOptions();
        }

        if (built.TryGetValue(name!, out var cached) && cached.Version == provider.UpdatedUtc)
        {
            return cached.Options;
        }

        var options = Build(name!, provider);
        built[name!] = (provider.UpdatedUtc, options);
        return options;
    }

    public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;

    private OpenIdConnectOptions Build(string name, OidcProvider provider)
    {
        var authority = new Uri(provider.Authority);
        var options = new OpenIdConnectOptions
        {
            Authority = provider.Authority,
            ClientId = provider.ClientId,
            ClientSecret = secrets.Unprotect(provider.ProtectedClientSecret),
            ResponseType = OpenIdConnectResponseType.Code,

            // Query, not form_post: the answer then arrives as a top-level GET, which carries SameSite=Lax cookies, so the
            // correlation and nonce cookies need neither SameSite=None nor HTTPS on a local instance.
            ResponseMode = OpenIdConnectResponseMode.Query,
            UsePkce = true,
            CallbackPath = OidcSchemes.CallbackPath(provider.Slug),
            SignInScheme = OidcSchemes.ExternalCookie,
            MapInboundClaims = false,
            GetClaimsFromUserInfoEndpoint = true,
            SaveTokens = false,

            // A plain-HTTP issuer only on this machine, which is what a test provider is. Anything else must use HTTPS.
            RequireHttpsMetadata = !(authority.IsLoopback || IPAddress.TryParse(authority.Host, out var ip) && IPAddress.IsLoopback(ip)),
            TimeProvider = time,
        };

        options.Scope.Clear();
        foreach (var scope in provider.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append("openid").Distinct(StringComparer.Ordinal))
        {
            options.Scope.Add(scope);
        }

        // Every claim of the user info answer, under its own name: the user name and groups claims are whatever the
        // provider page says, not a fixed list.
        options.ClaimActions.MapAll();
        options.TokenValidationParameters.NameClaimType = provider.UserNameClaim;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = PublicUrls.CookiePolicy(urls.Value);
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = PublicUrls.CookiePolicy(urls.Value);
        // Behind a proxy the request's own scheme and host are only as right as its forwarded headers. With a public base
        // URL configured, the redirect URI is built from it - the same URI the provider page tells people to register. The
        // handler keeps this value for redeeming the code, so both legs of the flow send the same one.
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            var configured = context.HttpContext.RequestServices.GetService<IOptions<PublicUrlOptions>>()?.Value.PublicBaseUrl;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                context.ProtocolMessage.RedirectUri = configured.TrimEnd('/') + OidcSchemes.CallbackPath(provider.Slug);
            }

            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<FiGet.Http.AuditLog>().Record(context.HttpContext, "signin.external.failed", provider.Slug, context.Failure?.Message);
            context.Response.Redirect("/account/login?external=failed");
            context.HandleResponse();
            return Task.CompletedTask;
        };

        new OpenIdConnectPostConfigureOptions(dataProtection).PostConfigure(name, options);
        return options;
    }
}

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("FiGet.OidcProvider.ClientSecret");

    public string Protect(string secret) => string.IsNullOrEmpty(secret) ? "" : protector.Protect(secret);

    public string? Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(protectedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
