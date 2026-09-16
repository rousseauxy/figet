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
                if (await JobLeaseGate.TakeAsync(scopes, time, JobLeaseNames.Retention, Interval, logger, stoppingToken))
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

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
        foreach (var feed in await feeds.ListAsync(cancellationToken))
        {
            if (!RetentionRules.Of(feed).Any)
            {
                continue;
            }

            try
            {
                var report = await retention.RunAsync(feed, cancellationToken);
                if (report.Total > 0)
                {
                    audit.Record(null, "retention.run", feed.Name, Describe(feed.Name, report));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retention on feed {Feed} failed; trying again next hour.", feed.Name);
            }
        }
    }

    public static string Describe(string feed, RetentionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"feed={feed} unlisted={report.Unlisted} deleted={report.Deleted} pruned={report.Pruned} freedBytes={report.FreedBytes}{(report.StoppedAtLimit ? " more=next-run" : "")}";
    }
}
