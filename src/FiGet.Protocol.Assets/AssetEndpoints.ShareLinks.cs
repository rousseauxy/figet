using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FiGet.Application.Assets;
using FiGet.Application.Ports;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;
using FiGet.Http;
using Microsoft.AspNetCore.Http;

namespace FiGet.Protocol.Assets;

/// <summary>
/// Download links that expire (<see cref="AssetShareLinks"/>): made by whoever may publish to a directory, used by
/// whoever holds the link. FiGet's own; the server being replaced has nothing like it, so no script depends on a shape.
/// </summary>
public static partial class AssetEndpoints
{
    /// <summary>
    /// One answer for every link that does not open a file - expired, altered, made by another server, or pointing at a
    /// file that was replaced or removed - so a link tells a stranger nothing about what is behind it.
    /// </summary>
    public const string LinkNotValid = "This download link is not valid, or it has expired.";

    /// <summary>The address a link token is used at.</summary>
    public static string ShareUrl(HttpContext http, string token) => $"{PublicUrls.Base(http)}/share/{token}";

    /// <summary>
    /// Makes a link for one file. Publishing rights are asked for, not reading: a link hands the file to someone the
    /// directory does not know, which is a decision about what the directory gives out.
    /// </summary>
    private static async Task<IResult> CreateLinkAsync(HttpContext http, string directory, string? path, string? hours, AssetService assets, AssetShareLinks links, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        return await MakeLinkAsync(http, request!.Feed, path, hours, assets, links, audit, cancellationToken) switch
        {
            (null, { } refusal) => Results.Text(refusal.Message, "text/plain", statusCode: refusal.Status),
            ({ } link, _) => Results.Json(new { url = link.Url, path = link.Path, expiresUtc = link.ExpiresUtc.UtcDateTime, hours = link.Hours }, Json),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    public sealed record CreatedLink(string Url, string Path, DateTimeOffset ExpiresUtc, int Hours);

    public sealed record LinkRefusal(int Status, string Message);

    /// <summary>
    /// Makes a link once the caller is known to be allowed. Shared with the browse page, so a link made from a button is
    /// the link a script gets, with the same limits and the same line in the audit log.
    /// </summary>
    public static async Task<(CreatedLink? Link, LinkRefusal? Refusal)> MakeLinkAsync(HttpContext http, Feed directory, string? path, string? hours, AssetService assets, AssetShareLinks links, AuditLog audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(audit);

        if (!links.Enabled)
        {
            return (null, new LinkRefusal(StatusCodes.Status404NotFound, "Download links are switched off on this server."));
        }

        var lifetime = links.DefaultHours;
        if (!string.IsNullOrWhiteSpace(hours)
            && (!int.TryParse(hours.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out lifetime) || lifetime < 1 || lifetime > links.MaxHours))
        {
            return (null, new LinkRefusal(StatusCodes.Status400BadRequest, $"hours must be a whole number from 1 to {links.MaxHours}."));
        }

        if (!AssetPath.TryParse(path, out var parsed)
            || parsed.IsRoot
            || await assets.FindAsync(directory, parsed, cancellationToken) is not { IsDirectory: false } file)
        {
            return (null, new LinkRefusal(StatusCodes.Status404NotFound, FileNotFound));
        }

        var (token, expires) = links.Create(directory, file, lifetime);
        audit.Record(http, "asset.link.create", file.Path, $"directory={directory.Name} hours={lifetime} expires={expires.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        return (new CreatedLink(ShareUrl(http, token), file.Path, expires, lifetime), null);
    }

    /// <summary>
    /// Serves the file a link was made for, to anyone who holds the link. The directory's network limit still applies -
    /// a link is not a way around where a directory may be reached from - and the file must still be the one the link
    /// was made for.
    /// </summary>
    private static async Task<IResult> SharedDownloadAsync(HttpContext http, string token, AssetShareLinks links, IFeedStore feeds, AssetService assets, AuditLog audit, CancellationToken cancellationToken)
    {
        // The link is the credential and it is in the address: keep it out of the Referer of anything the file links to,
        // out of shared caches, and out of search results.
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        if (links.Read(token) is not { } target
            || await feeds.FindAsync(target.Directory, cancellationToken) is not { Kind: FeedKind.Assets } directory
            || directory.Key != target.FeedKey)
        {
            return Results.Text(LinkNotValid, "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        if (!FeedAccess.ReachableFrom(http, directory))
        {
            audit.RecordThrottled(
                http,
                $"feed.network.refused|{directory.Key}|{RequestActor.Caller(http)}",
                TimeSpan.FromMinutes(10),
                "feed.network.refused",
                directory.Name,
                "path=/share");
            return Results.Json(new { error = "This asset directory is not reachable from your network address." }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!AssetPath.TryParse(target.Path, out var parsed)
            || await assets.FindAsync(directory, parsed, cancellationToken) is not { IsDirectory: false } file
            || !string.Equals(AssetShareLinks.ContentOf(file), target.Content, StringComparison.Ordinal)
            || await assets.OpenAsync(directory, file, cancellationToken) is not { } stream)
        {
            return Results.Text(LinkNotValid, "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        // Which link, without the link: a short hash tells two links for one file apart in the log and opens nothing.
        var linkId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12];
        audit.RecordThrottled(
            http,
            $"asset.link.use|{linkId}|{RequestActor.Caller(http)}",
            TimeSpan.FromHours(1),
            "asset.link.use",
            file.Path,
            $"directory={directory.Name} link={linkId}");

        return FileResult(http, file, stream);
    }
}
