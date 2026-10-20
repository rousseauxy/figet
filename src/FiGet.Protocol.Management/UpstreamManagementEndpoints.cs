using FiGet.Application.Feeds;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FiGet.Protocol.Management;

/// <summary>
/// A feed's upstreams over HTTP, under <c>/api/management/feeds/{name}/upstreams</c>. The rules are
/// <see cref="UpstreamAdminService"/>'s, the same the upstreams page uses; the caller is a service token with the admin
/// scope, so it is held to what an administrator may do, which still excludes an address on a block the server never
/// fetches from. An upstream is addressed by its id: its name can change.
/// </summary>
public static class UpstreamManagementEndpoints
{
    public static IEndpointRouteBuilder MapUpstreamManagement(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/feeds/{name}/upstreams");
        group.MapGet("", ListAsync);
        group.MapPost("", AddAsync).DisableAntiforgery();
        group.MapMethods("/{id:int}", [HttpMethods.Patch], UpdateAsync).DisableAntiforgery();
        group.MapPost("/{id:int}/move", MoveAsync).DisableAntiforgery();
        group.MapDelete("/{id:int}", RemoveAsync);
        return app;
    }

    /// <summary>
    /// An upstream to add, or the parts of one to change. Either <c>gallery</c> names a known public gallery, or <c>url</c>
    /// (and <c>protocol</c>, v3 by default) say where.
    /// </summary>
    public sealed record UpstreamRequest(
        string? Name,
        string? Gallery,
        string? Url,
        string? Protocol,
        string? Allow,
        string? Deny,
        string? CredentialRef,
        bool? Enabled);

    public sealed record MoveRequest(string? Direction);

    private sealed record UpstreamJson(int Id, string Name, string Url, string Protocol, bool Enabled, string? Allow, string? Deny, string? CredentialRef);

    private static async Task<IResult> ListAsync(HttpContext http, string name, IFeedStore feeds, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        return await feeds.FindAsync(name, cancellationToken) is { } feed
            ? Results.Json(feed.Upstreams.Select(Describe).ToList(), ManagementAccess.Json)
            : NotFound(name);
    }

    private static async Task<IResult> AddAsync(HttpContext http, string name, IFeedStore feeds, UpstreamAdminService upstreams, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        if (await ManagementAccess.ReadAsync<UpstreamRequest>(http, cancellationToken) is not { } request)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object: name, and either gallery or url (with protocol v2 or v3).");
        }

        if (!TryProtocol(request.Protocol, out var protocol))
        {
            return Refuse(StatusCodes.Status400BadRequest, "protocol is v2 or v3.");
        }

        var draft = new UpstreamDraft
        {
            Name = request.Name ?? "",
            Known = request.Gallery,
            Url = request.Url,
            Kind = protocol ?? UpstreamKind.V3,
            Allow = request.Allow,
            Deny = request.Deny,
            CredentialRef = request.CredentialRef,
        };

        var outcome = await upstreams.AddAsync(feed.Key, draft, callerIsAdmin: true, cancellationToken);
        if (!outcome.Succeeded)
        {
            return Refused(outcome.Status, feed.Name);
        }

        audit.Record(http, "upstream.add", outcome.Upstream!.Name, outcome.AuditDetail);
        return Results.Json(Describe(outcome.Upstream), ManagementAccess.Json, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>What is left out keeps its value. Naming a gallery replaces the address and the protocol with that gallery's.</summary>
    private static async Task<IResult> UpdateAsync(HttpContext http, string name, int id, IFeedStore feeds, UpstreamAdminService upstreams, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        if (feed.Upstreams.FirstOrDefault(u => u.Key == id) is not { } current)
        {
            return UpstreamNotFound(feed.Name, id);
        }

        if (await ManagementAccess.ReadAsync<UpstreamRequest>(http, cancellationToken) is not { } request)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object with the parts of the upstream to change.");
        }

        if (!TryProtocol(request.Protocol, out var protocol))
        {
            return Refuse(StatusCodes.Status400BadRequest, "protocol is v2 or v3.");
        }

        var draft = new UpstreamDraft
        {
            Name = request.Name ?? current.Name,
            Known = request.Gallery,
            Url = request.Url ?? current.Url,
            Kind = protocol ?? current.Kind,
            Allow = request.Allow ?? current.Allow,
            Deny = request.Deny ?? current.Deny,

            // An empty string takes the credential away; leaving the field out keeps it.
            CredentialRef = request.CredentialRef ?? current.CredentialRef,
            Enabled = request.Enabled ?? current.Enabled,
        };

        var outcome = await upstreams.UpdateAsync(feed.Key, id, draft, callerIsAdmin: true, cancellationToken);
        if (!outcome.Succeeded)
        {
            return outcome.Status == UpstreamChangeStatus.NotFound ? UpstreamNotFound(feed.Name, id) : Refused(outcome.Status, feed.Name);
        }

        audit.Record(http, "upstream.update", outcome.Upstream!.Name, outcome.AuditDetail);
        return Results.Json(Describe(outcome.Upstream), ManagementAccess.Json);
    }

    /// <summary>One place earlier (<c>up</c>) or later (<c>down</c>) in the order upstreams are asked in. Answers the new order.</summary>
    private static async Task<IResult> MoveAsync(HttpContext http, string name, int id, IFeedStore feeds, UpstreamAdminService upstreams, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        if (await ManagementAccess.ReadAsync<MoveRequest>(http, cancellationToken) is not { Direction: "up" or "down" } request)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object: direction, up or down.");
        }

        if (feed.Upstreams.All(u => u.Key != id))
        {
            return UpstreamNotFound(feed.Name, id);
        }

        // Already first or last is not an error: the upstream is where the caller wanted it.
        var outcome = await upstreams.MoveAsync(feed.Key, id, request.Direction == "up", cancellationToken);
        if (outcome.Succeeded)
        {
            audit.Record(http, "upstream.move", outcome.Upstream!.Name, outcome.AuditDetail);
        }

        var after = await feeds.FindAsync(feed.Name, cancellationToken);
        return Results.Json((after ?? feed).Upstreams.Select(Describe).ToList(), ManagementAccess.Json);
    }

    /// <summary>Removes the upstream and the listings cached from it; packages already cached stay in the feed.</summary>
    private static async Task<IResult> RemoveAsync(HttpContext http, string name, int id, IFeedStore feeds, UpstreamAdminService upstreams, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        var outcome = await upstreams.RemoveAsync(feed.Key, id, cancellationToken);
        if (!outcome.Succeeded)
        {
            return UpstreamNotFound(feed.Name, id);
        }

        audit.Record(http, "upstream.remove", outcome.Upstream!.Name, outcome.AuditDetail);
        return Results.NoContent();
    }

    private static Task<IResult?> AdminAsync(HttpContext http, CancellationToken cancellationToken) =>
        ManagementAccess.AdminAsync(http, "feed management", "Managing feeds takes a service token with the admin scope.", cancellationToken);

    private static IResult Refuse(int status, string message) => ManagementAccess.Refuse(status, message);

    private static IResult NotFound(string name) =>
        Refuse(StatusCodes.Status404NotFound, $"There is no feed or asset directory named '{name}'.");

    private static IResult UpstreamNotFound(string feed, int id) =>
        Refuse(StatusCodes.Status404NotFound, $"The feed '{feed}' has no upstream with id {id}.");

    private static IResult Refused(UpstreamChangeStatus status, string feed) => status switch
    {
        UpstreamChangeStatus.NameTaken => Refuse(StatusCodes.Status409Conflict, $"Another upstream of '{feed}' already has that name."),
        UpstreamChangeStatus.CredentialNotAllowed => Refuse(StatusCodes.Status400BadRequest, $"credentialRef names an environment variable starting with {FeedUpstream.CredentialPrefix}, in upper case letters, digits and underscores."),
        UpstreamChangeStatus.UrlNotAllowed => Refuse(StatusCodes.Status400BadRequest, "url must be an absolute http or https address, and not one this server never fetches from (link-local, reserved)."),
        UpstreamChangeStatus.NotFound => NotFound(feed),
        UpstreamChangeStatus.NotAPackageFeed => Refuse(StatusCodes.Status409Conflict, "An asset directory has no upstreams."),
        _ => Refuse(StatusCodes.Status400BadRequest, $"An upstream needs a name and either a url or a gallery: {string.Join(", ", FeedUpstream.Known.Select(k => k.Name))}."),
    };

    private static bool TryProtocol(string? text, out UpstreamKind? protocol)
    {
        protocol = text?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "v2" => UpstreamKind.V2,
            "v3" => UpstreamKind.V3,
            _ => (UpstreamKind)(-1),
        };

        return protocol is null || Enum.IsDefined(protocol.Value);
    }

    private static UpstreamJson Describe(FeedUpstream upstream) => new(
        upstream.Key,
        upstream.Name,
        upstream.Url,
        upstream.Kind == UpstreamKind.V2 ? "v2" : "v3",
        upstream.Enabled,
        upstream.Allow.Length == 0 ? null : upstream.Allow,
        upstream.Deny.Length == 0 ? null : upstream.Deny,
        upstream.CredentialRef);
}
