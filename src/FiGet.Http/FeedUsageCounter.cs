using System.Collections.Concurrent;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FiGet.Http;

/// <summary>
/// Counts downloads and searches per feed and hour in memory; a background writer moves them to the database every minute.
/// A download costs an increment, never a database write, and each replica adds its own counts to the same rows.
/// </summary>
public sealed class FeedUsageCounter(TimeProvider time)
{
    /// <summary>Set by <see cref="FeedAccess"/> on a read it let through: only those are counted.</summary>
    public const string FeedKeyItem = "figet:usage-feed";

    private readonly ConcurrentDictionary<(int FeedKey, DateTime HourUtc, FeedUsageKind Kind), long> counts = new();

    public void Record(int feedKey, FeedUsageKind kind)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        counts.AddOrUpdate((feedKey, hour, kind), 1, (_, count) => count + 1);
    }

    /// <summary>Takes everything counted so far; what is counted meanwhile waits for the next drain.</summary>
    public IReadOnlyList<FeedUsageCount> Drain()
    {
        var drained = new List<FeedUsageCount>();
        foreach (var key in counts.Keys)
        {
            if (counts.TryRemove(key, out var count))
            {
                drained.Add(new FeedUsageCount(key.FeedKey, key.HourUtc, key.Kind, count));
            }
        }

        return drained;
    }

    /// <summary>Puts counts back that could not be stored, so a database hiccup loses nothing but time.</summary>
    public void Restore(IEnumerable<FeedUsageCount> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        foreach (var count in failed)
        {
            counts.AddOrUpdate((count.FeedKey, count.HourUtc, count.Kind), count.Count, (_, existing) => existing + count.Count);
        }
    }

    /// <summary>Counts a finished request when it was a successful download or search on a feed it was allowed to read.</summary>
    public void Observe(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Items.TryGetValue(FeedKeyItem, out var value) && value is int feedKey && Classify(http) is { } kind)
        {
            Record(feedKey, kind);
        }
    }

    /// <summary>
    /// What a request was, from the route it matched. A search counts only its first page: a <c>Find-Module</c> pages through
    /// forty entries at a time, and counting every page would make a feed with big packages look busy. HEAD, a count-only
    /// request, a failed one, a resumed download and a revalidation that answered 304 are not uses.
    /// </summary>
    public static FeedUsageKind? Classify(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var request = http.Request;
        if (!HttpMethods.IsGet(request.Method)
            || http.Response.StatusCode is not (StatusCodes.Status200OK or StatusCodes.Status206PartialContent)
            || (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is not { } route)
        {
            return null;
        }

        if (route.EndsWith("/package/{id}/{version}", StringComparison.Ordinal)
            || (route.EndsWith("/v3/flatcontainer/{id}/{version}/{file}", StringComparison.Ordinal)
                && request.Path.Value?.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true)
            || (route.StartsWith("/api/packages/", StringComparison.Ordinal) && route.EndsWith("/download", StringComparison.Ordinal))
            || route.EndsWith("/content/{**path}", StringComparison.Ordinal))
        {
            var range = request.Headers.Range.ToString();
            return range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase) ? FeedUsageKind.Download : null;
        }

        var firstPage = IsFirstPage(request.Query["$skip"]) && IsFirstPage(request.Query["skip"]);
        if ((route.EndsWith("/Search()", StringComparison.Ordinal)
                || route.EndsWith("/FindPackagesById()", StringComparison.Ordinal)
                || route.EndsWith("/Packages()", StringComparison.Ordinal)
                || route.EndsWith("/Packages", StringComparison.Ordinal)
                || route.EndsWith("/v3/query", StringComparison.Ordinal))
            && firstPage)
        {
            return FeedUsageKind.Search;
        }

        return route.EndsWith("/v3/flatcontainer/{id}/index.json", StringComparison.Ordinal)
            || route.EndsWith("/v3/registration/{id}/index.json", StringComparison.Ordinal)
            || (route.StartsWith("/api/packages/", StringComparison.Ordinal)
                && (route.EndsWith("/versions", StringComparison.Ordinal) || route.EndsWith("/latest", StringComparison.Ordinal)))
            || route.EndsWith("/dir/{**path}", StringComparison.Ordinal)
                ? FeedUsageKind.Search
                : null;
    }

    private static bool IsFirstPage(string? skip) =>
        string.IsNullOrWhiteSpace(skip) || (int.TryParse(skip, out var value) && value <= 0);
}
