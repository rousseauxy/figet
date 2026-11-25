using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Runs every feed's retention and cache pruning once an hour, on the one replica that holds the job's lease. Two runs at
/// once would remove nothing twice - a version already removed is simply not found by the second - but each is a scan of
/// every feed, and each wrote its own audit entries.
/// </summary>
public sealed class RetentionJobService(
    IServiceScopeFactory scopes,
    AuditLog audit,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<RetentionJobService> logger) : BackgroundService
{
    private TimeSpan Interval => options.Value.Jobs.Retention;

    /// <summary>Not at start-up: a restart loop must not turn into a removal loop, and migrations may still be settling.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                await JobLeaseGate.RunAsync(scopes, time, JobLeaseNames.Retention, Interval, logger, RunOnceAsync, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Applies every feed's rules. Returns the line the jobs table shows.</summary>
    public async Task<string> RunOnceAsync(CancellationToken cancellationToken)
    {
        int feedsWithRules = 0, failed = 0, unlisted = 0, deleted = 0, pruned = 0;
        long freed = 0;
        await using var scope = scopes.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
        foreach (var feed in await feeds.ListAsync(cancellationToken))
        {
            if (!RetentionRules.Of(feed).Any)
            {
                continue;
            }

            feedsWithRules++;
            try
            {
                var report = await retention.RunAsync(feed, cancellationToken);
                (unlisted, deleted, pruned, freed) = (unlisted + report.Unlisted, deleted + report.Deleted, pruned + report.Pruned, freed + report.FreedBytes);
                if (report.Total > 0)
                {
                    audit.Record(null, "retention.run", feed.Name, Describe(feed.Name, report));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "Retention on feed {Feed} failed; trying again next hour.", feed.Name);
            }
        }

        if (feedsWithRules == 0)
        {
            return "No feed has retention rules or cache pruning.";
        }

        var done = unlisted + deleted + pruned == 0
            ? $"Nothing to do on {Feeds(feedsWithRules)}."
            : $"{Feeds(feedsWithRules)}: {unlisted} unlisted, {deleted} deleted, {pruned} cached copies pruned, {FiGet.Web.Components.Shared.Display.Size(freed)} freed.";
        return failed == 0 ? done : $"{done} Failed on {Feeds(failed)}; see the server log.";
    }

    private static string Feeds(int count) => count == 1 ? "1 feed" : $"{count} feeds";

    public static string Describe(string feed, RetentionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"feed={feed} unlisted={report.Unlisted} deleted={report.Deleted} pruned={report.Pruned} freedBytes={report.FreedBytes}{(report.StoppedAtLimit ? " more=next-run" : "")}";
    }
}
