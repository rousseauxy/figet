using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Sweeps away multipart uploads nobody completed: a client that crashed half way leaves its parts on shared
/// storage, and nothing else would ever remove them. One replica sweeps, the one holding the lease: a sweep lists the
/// whole upload area of the shared volume, and every replica doing it each hour bought nothing.
/// </summary>
public sealed class AssetUploadCleanupService(
    IAssetStorage storage,
    IServiceScopeFactory scopes,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<AssetUploadCleanupService> logger) : BackgroundService
{
    private TimeSpan Interval => options.Value.Jobs.UploadSweep;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            if (!await JobLeaseGate.TakeAsync(scopes, time, JobLeaseNames.UploadSweep, Interval, logger, stoppingToken))
            {
                continue;
            }

            try
            {
                var cutoff = time.GetUtcNow().UtcDateTime - options.Value.Assets.IncompleteUploadExpiry;
                var removed = await storage.PruneUploadsAsync(cutoff, stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Removed {Count} multipart upload(s) that were never completed.", removed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not sweep abandoned multipart uploads; trying again in an hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
