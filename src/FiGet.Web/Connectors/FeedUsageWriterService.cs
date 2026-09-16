using FiGet.Application.Ports;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;
using FiGet.Domain.Entities;
using FiGet.Http;

namespace FiGet.Web.Connectors;

/// <summary>
/// Moves what <see cref="FeedUsageCounter"/> counted into the database once a minute, and on shutdown. Every replica writes
/// its own counts; the one holding the lease removes hours older than <see cref="Retention"/> once a day.
/// </summary>
public sealed class FeedUsageWriterService(
    FeedUsageCounter counter,
    IServiceScopeFactory scopes,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<FeedUsageWriterService> logger) : BackgroundService
{
    /// <summary>
    /// Not a setting: this is the write path, not a schedule. Counts live in memory until it runs, so switching it off
    /// would lose them rather than defer them.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private TimeSpan PruneInterval => options.Value.Jobs.UsagePrune;

    /// <summary>What the graph can show at most is thirty days; the rest is kept for a longer view later, and then goes.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private DateTime lastPrune = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(counter, scopes, logger, stoppingToken);
                await PruneIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(counter, scopes, logger, drain.Token);
    }

    /// <summary>Stores what was counted. Counts that could not be stored go back to the counter for the next attempt.</summary>
    public static async Task FlushAsync(FeedUsageCounter counter, IServiceScopeFactory scopes, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(scopes);
        var counts = counter.Drain();
        if (counts.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IFeedUsageStore>().AddAsync(counts, cancellationToken);
        }
        catch (Exception ex)
        {
            counter.Restore(counts);
            logger.LogWarning(ex, "Could not store {Count} usage count(s); trying again in a minute.", counts.Count);
        }
    }

    private async Task PruneIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - lastPrune < PruneInterval)
        {
            return;
        }

        lastPrune = now;
        if (!await JobLeaseGate.TakeAsync(scopes, time, JobLeaseNames.UsagePrune, PruneInterval, logger, cancellationToken))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IFeedUsageStore>().PruneAsync(now - Retention, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not remove old usage counts; trying again tomorrow.");
        }
    }
}
