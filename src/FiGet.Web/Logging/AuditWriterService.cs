using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Logging;

/// <summary>
/// Stores what <see cref="AuditLog"/> queued, in batches, and prunes entries past the retention. Every replica writes its
/// own entries; the one holding the lease prunes.
/// </summary>
public sealed class AuditWriterService(
    AuditLog audit,
    IServiceScopeFactory scopes,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<AuditWriterService> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private TimeSpan PruneInterval => options.Value.Jobs.AuditPrune;
    private DateTime lastPrune = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await audit.Pending.WaitToReadAsync(stoppingToken))
            {
                await WriteAvailableAsync(stoppingToken);
                await PruneIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Shutting down: store what is already queued rather than lose it with the process.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await WriteAvailableAsync(drain.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stopped before {Count} queued audit entries were stored; they are in the console log only.", audit.Pending.Count);
        }
    }

    private async Task WriteAvailableAsync(CancellationToken cancellationToken)
    {
        var batch = new List<AuditEntry>(BatchSize);
        while (audit.Pending.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count == BatchSize)
            {
                await StoreAsync(batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await StoreAsync(batch, cancellationToken);
        }

        if (audit.TakeDropped() is > 0 and var dropped)
        {
            logger.LogError("{Count} audit entries were dropped because the queue was full; they are in the console log only.", dropped);
        }
    }

    /// <summary>Three attempts a few seconds apart; after that the batch is given up, loudly.</summary>
    private async Task StoreAsync(List<AuditEntry> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IAuditStore>().AddRangeAsync(batch, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == 3)
                {
                    logger.LogError(ex, "Could not store {Count} audit entries after three attempts; they are in the console log only.", batch.Count);
                    return;
                }

                logger.LogWarning(ex, "Could not store {Count} audit entries; trying again.", batch.Count);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), time, cancellationToken);
            }
        }
    }

    private async Task PruneIfDueAsync(CancellationToken cancellationToken)
    {
        var days = options.Value.Audit.RetentionDays;
        var now = time.GetUtcNow().UtcDateTime;
        if (days <= 0 || now - lastPrune < PruneInterval)
        {
            return;
        }

        lastPrune = now;
        if (!await Connectors.JobLeaseGate.TakeAsync(scopes, time, JobLeaseNames.AuditPrune, PruneInterval, logger, cancellationToken))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var removed = await scope.ServiceProvider.GetRequiredService<IAuditStore>().PruneAsync(now.AddDays(-days), cancellationToken);
            if (removed > 0)
            {
                logger.LogInformation("Pruned {Count} audit entries older than {Days} days.", removed, days);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not prune the audit log; trying again later.");
        }
    }
}
