using System.Security.Claims;
using System.Text.Json;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace FiGet.Web.SignIn;

/// <summary>
/// Sign-in and account linking through OpenID Connect providers. The provider's handler signs its answer in to the short
/// external cookie; <c>/account/external/complete</c> then decides whose account that is, and issues the real sign-in.
/// </summary>
public static class SignInEndpoints
{
    private const string ProviderItem = "figet:provider";
    private const string LinkItem = "figet:link";
    private const string ReturnItem = "figet:return";
    private const string CompletePath = "/account/external/complete";

    public static void MapExternalSignIn(this WebApplication app)
    {
        // A button on the sign-in page. A POST with the page's antiforgery token, so no other site can start a sign-in with
        // an identity of its choosing in this browser.
        app.MapPost("/account/external/{slug}", async (string slug, HttpContext http, IOidcProviderStore providers) =>
        {
            if (!await ValidAsync(http))
            {
                return Results.Redirect("/account/login?external=expired");
            }

            var provider = await providers.FindBySlugAsync(slug, http.RequestAborted);
            if (provider is not { Enabled: true })
            {
                return Results.Redirect("/account/login?external=unknown");
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            return Challenge(provider, returnUrl: LocalOrRoot(form["returnUrl"].ToString()), linkTo: null);
        });

        // Connect a provider to the signed-in account, from its profile page.
        app.MapPost("/account/external/{slug}/link", async (string slug, HttpContext http, IOidcProviderStore providers) =>
        {
            var actor = FiGetApp.Actor(http.User);
            if (actor is null || !await ValidAsync(http))
            {
                return Results.Redirect("/account/profile?linked=expired");
            }

            var provider = await providers.FindBySlugAsync(slug, http.RequestAborted);
            return provider is not { Enabled: true }
                ? Results.Redirect("/account/profile?linked=unknown")
                : Challenge(provider, returnUrl: "/account/profile", linkTo: actor.Key);
        }).RequireAuthorization();

        app.MapGet(CompletePath, CompleteAsync);

        app.MapPost("/account/external/logins/{key:int}/remove", async (
            int key,
            HttpContext http,
            ExternalAccountService external,
            AuditLog audit) =>
        {
            var actor = FiGetApp.Actor(http.User);
            if (actor is null || !await ValidAsync(http))
            {
                return Results.Redirect("/account/profile");
            }

            var outcome = await external.UnlinkAsync(actor.Key, key, http.RequestAborted);
            if (outcome == UnlinkStatus.Removed)
            {
                audit.Record(http, "account.unlink", http.User.Identity?.Name ?? "", $"login={key}");
            }

            return Results.Redirect("/account/profile?unlinked=" + (outcome == UnlinkStatus.LastWayToSignIn ? "last" : "ok"));
        }).RequireAuthorization();
    }

    private static async Task<IResult> CompleteAsync(
        HttpContext http,
        IOidcProviderStore providers,
        ExternalAccountService external,
        AuditLog audit)
    {
        var result = await http.AuthenticateAsync(OidcSchemes.ExternalCookie);
        await http.SignOutAsync(OidcSchemes.ExternalCookie);
        if (!result.Succeeded || result.Properties?.Items.TryGetValue(ProviderItem, out var slug) != true || slug is null)
        {
            return Results.Redirect("/account/login?external=failed");
        }

        var provider = await providers.FindBySlugAsync(slug, http.RequestAborted);
        var identity = provider is { Enabled: true } ? IdentityFrom(result.Principal!, provider) : null;
        if (provider is null || identity is null)
        {
            audit.Record(http, "signin.external.failed", slug, "no provider or no subject");
            return Results.Redirect("/account/login?external=failed");
        }

        if (result.Properties.Items.TryGetValue(LinkItem, out var link) && link is not null)
        {
            // Only for the account that asked: the sign-in cookie must still be that account's when the provider answers.
            var actor = FiGetApp.Actor(http.User);
            if (actor is null || actor.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) != link)
            {
                return Results.Redirect("/account/login?external=failed");
            }

            var status = await external.LinkAsync(actor.Key, provider, identity, http.RequestAborted);
            audit.Record(http, "account.link", http.User.Identity?.Name ?? "", $"provider={provider.Slug} status={status}");
            return Results.Redirect("/account/profile?linked=" + status switch
            {
                LinkStatus.Linked => "ok",
                LinkStatus.AlreadyYours => "already",
                _ => "taken",
            });
        }

        var outcome = await external.SignInAsync(provider, identity, http.RequestAborted);
        if (outcome.Status == ExternalSignInStatus.Disabled)
        {
            audit.Record(http, "signin.disabled", outcome.User!.UserName, $"provider={provider.Slug}");
            return Results.Redirect("/account/login?external=disabled");
        }

        await FiGetApp.SignInUserAsync(http, outcome.User!);
        audit.Record(
            http,
            outcome.Status == ExternalSignInStatus.Created ? "signin.external.created" : "signin.external",
            outcome.User!.UserName,
            $"provider={provider.Slug}");
        return Results.Redirect(result.Properties.Items.TryGetValue(ReturnItem, out var back) ? LocalOrRoot(back) : "/");
    }

    /// <summary>What the provider said, from the ID token and the user info answer together. Null without a subject.</summary>
    public static ExternalIdentity? IdentityFrom(ClaimsPrincipal principal, OidcProvider provider)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(provider);
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        IReadOnlyCollection<string>? groups = null;
        if (!string.IsNullOrWhiteSpace(provider.GroupsClaim))
        {
            groups = principal.FindAll(provider.GroupsClaim).SelectMany(c => Values(c.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return new ExternalIdentity(
            subject,
            principal.FindFirst(provider.UserNameClaim)?.Value,
            principal.FindFirst("email")?.Value,
            principal.FindFirst("name")?.Value,
            groups);
    }

    private static IResult Challenge(OidcProvider provider, string returnUrl, int? linkTo)
    {
        var properties = new AuthenticationProperties { RedirectUri = CompletePath };
        properties.Items[ProviderItem] = provider.Slug;
        properties.Items[ReturnItem] = returnUrl;
        if (linkTo is not null)
        {
            properties.Items[LinkItem] = linkTo.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Results.Challenge(properties, [OidcSchemes.Name(provider.Slug)]);
    }

    /// <summary>A groups claim is one claim per group, or - from some user info answers - one claim holding a JSON array.</summary>
    private static IEnumerable<string> Values(string value)
    {
        if (value.StartsWith('['))
        {
            string[]? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<string[]>(value);
            }
            catch (JsonException)
            {
            }

            if (parsed is not null)
            {
                return parsed.Where(v => !string.IsNullOrWhiteSpace(v));
            }
        }

        return string.IsNullOrWhiteSpace(value) ? [] : [value];
    }

    private static Task<bool> ValidAsync(HttpContext http) =>
        http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http);

    private static string LocalOrRoot(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
