using System.Globalization;
using FiGet.Application.Ports;
using FiGet.Application.Reports;
using FiGet.Domain.Entities;
using FiGet.Http;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Posts each feed's change report to its webhook, on the one replica holding the lease.
///
/// The window is "since we last reported on this feed", not "the last day": a restart, a failover or a server that was
/// off over a weekend must neither skip what happened nor say it twice. A failure leaves that mark where it was, so
/// the next run covers what this one missed - which is the retry, and costs nothing.
/// </summary>
public sealed class ChangeReportJobService(
    IServiceScopeFactory scopes,
    ChangeWebhookFactory webhooks,
    AuditLog audit,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<ChangeReportJobService> logger) : BackgroundService
{
    /// <summary>Not at start-up: a restart loop must not become a stream of reports.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    private TimeSpan Interval => options.Value.Jobs.ChangeReport;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                if (await JobLeaseGate.TakeAsync(scopes, time, JobLeaseNames.ChangeReport, Interval, logger, stoppingToken))
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Reports on every feed that has somewhere to report to. One feed's failure does not stop the others.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        foreach (var feed in await feeds.ListAsync(cancellationToken))
        {
            if (feed.Kind == FeedKind.Assets || !Reported(feed))
            {
                continue;
            }

            try
            {
                await SendAsync(feed, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The change report for feed {Feed} could not be built; trying again next run.", feed.Name);
            }
        }
    }

    /// <summary>
    /// Builds and posts one feed's report. Returns what happened, so the page's "send now" can say it.
    /// </summary>
    public async Task<ChangeReportOutcome> SendAsync(Feed feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingStore>();
        var reports = scope.ServiceProvider.GetRequiredService<ChangeReportService>();

        var webhook = await webhooks.ForAsync(feed, cancellationToken);
        if (webhook is null)
        {
            return ChangeReportOutcome.NoWebhook;
        }

        using var sender = webhook as IDisposable;
        var now = time.GetUtcNow().UtcDateTime;
        var from = await ReportedThroughAsync(settings, feed, now, cancellationToken);
        var report = await reports.BuildAsync(feed, from, now, cancellationToken);
        if (!report.Any && !options.Value.Changes.Webhook.SendWhenEmpty)
        {
            // Silence means nothing moved, which is the whole reason a daily report is bearable.
            await settings.SetAsync(ChangeWebhookFactory.ReportedThroughKey(feed.Key), now.ToString("o", CultureInfo.InvariantCulture), null, cancellationToken);
            return ChangeReportOutcome.NothingToSay;
        }

        var body = ChangeReportBody.Write(report, webhooks.Format(), feed.ChangeTarget, now);
        var result = await webhook.PostAsync(body, cancellationToken);
        audit.Record(
            null,
            "changes.report",
            feed.Name,
            $"feed={feed.Name} rows={report.Changes.Count} from={from:o} to={now:o} host={result.Host} outcome={(result.Delivered ? "ok" : "failed")}");

        if (!result.Delivered)
        {
            // The mark stays where it was on purpose: the next run's window still covers what this one could not send.
            logger.LogWarning("The change report for feed {Feed} was not delivered to {Host}: {Problem}", feed.Name, result.Host, result.Problem);
            return ChangeReportOutcome.Failed;
        }

        await settings.SetAsync(ChangeWebhookFactory.ReportedThroughKey(feed.Key), now.ToString("o", CultureInfo.InvariantCulture), null, cancellationToken);
        return ChangeReportOutcome.Sent;
    }

    /// <summary>Whether this feed is one of the feeds reported on; an empty list means every package feed.</summary>
    private bool Reported(Feed feed)
    {
        var named = options.Value.Changes.Webhook.Feeds;
        return named.Count == 0 || named.Any(n => string.Equals(n?.Trim(), feed.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where the window starts: after the last report, floored at the longest window allowed, so an instance that was
    /// off for a month posts a week of change rather than a month of it.
    /// </summary>
    private async Task<DateTime> ReportedThroughAsync(ISettingStore settings, Feed feed, DateTime now, CancellationToken cancellationToken)
    {
        var floor = now.AddDays(-Math.Max(1, options.Value.Changes.Webhook.MaxDays));
        var stored = await settings.GetAsync(ChangeWebhookFactory.ReportedThroughKey(feed.Key), cancellationToken);
        if (!DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var through))
        {
            return floor;
        }

        return through.ToUniversalTime() < floor ? floor : through.ToUniversalTime();
    }
}

/// <summary>What one attempt did, in the words a page can show.</summary>
public enum ChangeReportOutcome
{
    /// <summary>No address is configured for this feed or this server.</summary>
    NoWebhook,

    /// <summary>Nothing changed, so nothing was posted.</summary>
    NothingToSay,

    Sent,
    Failed,
}
