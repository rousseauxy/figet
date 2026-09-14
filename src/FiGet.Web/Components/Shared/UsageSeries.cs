using System.Globalization;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Web.Components.Shared;

/// <summary>A period the usage graph can show, and how it is cut into points.</summary>
public sealed record UsageRange(string Code, string Label, TimeSpan Span, TimeSpan Bucket)
{
    public static IReadOnlyList<UsageRange> All { get; } =
    [
        new("24h", "24 hours", TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
        new("7d", "7 days", TimeSpan.FromDays(7), TimeSpan.FromHours(6)),
        new("30d", "30 days", TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
    ];

    public static UsageRange Default => All[1];

    public static UsageRange Parse(string? code) => All.FirstOrDefault(r => r.Code == code) ?? Default;
}

/// <summary>One feed's line: its count per point, oldest first, and the total.</summary>
public sealed record UsageLine(Feed Feed, int Color, IReadOnlyList<long> Points, long Total);

/// <summary>The graph's numbers, computed apart from the markup so they can be tested without rendering.</summary>
public sealed record UsageSeries(IReadOnlyList<DateTime> Starts, IReadOnlyList<UsageLine> Lines, long Max)
{
    /// <summary>
    /// Cuts the stored hours into the range's points. The last point ends with the current hour, so the line reaches now;
    /// points are aligned to whole buckets in UTC, so the same hour always falls in the same point.
    /// </summary>
    public static UsageSeries Build(IReadOnlyList<Feed> feeds, IReadOnlyList<FeedUsageCount> hours, UsageRange range, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(hours);
        ArgumentNullException.ThrowIfNull(range);
        var bucketTicks = range.Bucket.Ticks;
        var currentHour = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
        var lastStart = new DateTime(currentHour.Ticks - (currentHour.Ticks % bucketTicks), DateTimeKind.Utc);
        var count = (int)(range.Span.Ticks / bucketTicks);
        var firstStart = lastStart.AddTicks(-(count - 1) * bucketTicks);
        var starts = Enumerable.Range(0, count).Select(i => firstStart.AddTicks(i * bucketTicks)).ToList();

        var byFeed = hours
            .Where(h => h.HourUtc >= firstStart)
            .GroupBy(h => h.FeedKey)
            .ToDictionary(g => g.Key, g => g.ToList());

        var lines = feeds.Select(feed =>
        {
            var points = new long[count];
            foreach (var hour in byFeed.GetValueOrDefault(feed.Key) ?? [])
            {
                var index = (int)((hour.HourUtc.Ticks - firstStart.Ticks) / bucketTicks);
                if (index >= 0 && index < count)
                {
                    points[index] += hour.Count;
                }
            }

            return new UsageLine(feed, FeedColors.Of(feed), points, points.Sum());
        }).ToList();

        return new UsageSeries(starts, lines, lines.Count == 0 ? 0 : lines.Max(l => l.Points.Count == 0 ? 0 : l.Points.Max()));
    }

    /// <summary>A rounded top for the scale, so the axis reads 10, 50, 200 rather than 37.</summary>
    public long ScaleTop()
    {
        if (Max <= 4)
        {
            return 4;
        }

        var magnitude = (long)Math.Pow(10, Math.Floor(Math.Log10(Max)));
        foreach (var step in new long[] { 1, 2, 5, 10 })
        {
            if (step * magnitude >= Max)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>The polyline of a line in a 1000 by 200 box: the SVG stretches it to the width it is given.</summary>
    public static string PolylinePoints(UsageLine line, long top)
    {
        ArgumentNullException.ThrowIfNull(line);
        var n = line.Points.Count;
        return string.Join(' ', line.Points.Select((value, i) =>
        {
            var x = n == 1 ? 0 : i * 1000.0 / (n - 1);
            var y = 200 - (top == 0 ? 0 : value * 196.0 / top);
            return x.ToString("0.#", CultureInfo.InvariantCulture) + "," + y.ToString("0.#", CultureInfo.InvariantCulture);
        }));
    }
}
