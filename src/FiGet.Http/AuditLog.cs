using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FiGet.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FiGet.Http;

/// <summary>
/// Who changed what, and when. Separate from the request log on purpose: that one answers whether a client
/// reached this server, this one answers who changed something - a different question, a different
/// audience, and a different retention need.
///
/// Every entry goes two ways from the same call. A console line under its own category, as before: on wherever
/// Information is on, silenced with <c>Logging:LogLevel:FiGet.Audit=None</c>. And a row in the database, so the answer
/// survives the container's log rotating: <see cref="Record"/> only queues it, and a background writer stores queued
/// entries in batches, so a request never waits on the audit table and a slow database never slows a push. The console
/// line is written first and does not depend on the row.
/// </summary>
public sealed partial class AuditLog(ILoggerFactory loggers, TimeProvider time)
{
    /// <summary>The logging category every audit line is written under.</summary>
    public const string Category = "FiGet.Audit";

    /// <summary>
    /// Enough for a long burst of changes while the database is slow. When it is full the oldest queued entry makes room,
    /// and the writer says so in the log: losing audit rows silently is the failure this table exists to prevent.
    /// </summary>
    public const int Capacity = 10_000;

    private readonly ILogger logger = loggers.CreateLogger(Category);
    private readonly ConcurrentDictionary<string, DateTime> throttled = new(StringComparer.Ordinal);
    private long dropped;

    private readonly Channel<AuditEntry> queue = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    /// <summary>What the database writer reads.</summary>
    public ChannelReader<AuditEntry> Pending => queue.Reader;

    /// <summary>How many entries were pushed out of a full queue since the last call, which resets it.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref dropped, 0);

    /// <summary>
    /// Records one change. <paramref name="action"/> is a dotted verb such as <c>feed.create</c>, and
    /// <paramref name="subject"/> is what it acted on - a feed name, a token name, a package id.
    /// </summary>
    public void Record(HttpContext? http, string action, string subject, string? detail = null)
    {
        var actor = RequestActor.Describe(http);
        var caller = RequestActor.Caller(http);
        logger.LogInformation(
            "{Action} {Subject} by {Actor} from {Caller}{Detail}",
            action,
            subject,
            actor,
            caller,
            string.IsNullOrEmpty(detail) ? "" : " | " + detail);

        var feed = FeedOf(action, subject, detail);
        var entry = new AuditEntry
        {
            WhenUtc = time.GetUtcNow().UtcDateTime,
            Action = Truncate(action.ToLowerInvariant(), 64),
            Subject = Truncate(subject, 256),
            Actor = Truncate(actor, 192),
            ActorLower = Truncate(actor.ToLowerInvariant(), 192),
            Feed = feed is null ? null : Truncate(feed, 64),
            FeedLower = feed is null ? null : Truncate(feed.ToLowerInvariant(), 64),
            Detail = Truncate(detail ?? "", 2000),
            Caller = Truncate(caller, 128),
        };

        if (queue.Reader.Count >= Capacity)
        {
            Interlocked.Increment(ref dropped);
        }

        queue.Writer.TryWrite(entry);
    }

    /// <summary>
    /// Records an event at most once per <paramref name="window"/> for the same <paramref name="key"/>. For what a client
    /// repeats on every request - a dead key sent by a scheduled job - where one line a window says it all.
    /// </summary>
    public void RecordThrottled(HttpContext? http, string key, TimeSpan window, string action, string subject, string? detail = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (throttled.TryGetValue(key, out var last) && now - last < window)
        {
            return;
        }

        throttled[key] = now;
        if (throttled.Count > 10_000)
        {
            foreach (var old in throttled.Where(pair => now - pair.Value >= window).Select(pair => pair.Key).ToList())
            {
                throttled.TryRemove(old, out _);
            }
        }

        Record(http, action, subject, detail);
    }

    /// <summary>
    /// The feed or asset directory an entry is about, for filtering: named in the detail as <c>feed=</c> or
    /// <c>directory=</c> by the call sites, or the subject itself for a change to a feed.
    /// </summary>
    internal static string? FeedOf(string action, string subject, string? detail)
    {
        if (!string.IsNullOrEmpty(detail) && FeedInDetail().Match(detail) is { Success: true } match)
        {
            return match.Groups["feed"].Value;
        }

        return action.StartsWith("feed.", StringComparison.OrdinalIgnoreCase) ? subject : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"(?:^|\s)(?:feed|directory)=(?<feed>[^\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FeedInDetail();
}
