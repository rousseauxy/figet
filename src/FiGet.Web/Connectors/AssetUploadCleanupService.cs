using FiGet.Application.Ports;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Sweeps away multipart uploads nobody completed: a client that crashed half way leaves its parts on shared
/// storage, and nothing else would ever remove them. Every replica sweeps; that is harmless, because a sweep
/// only removes uploads that have been idle for the whole expiry, and removing one twice removes nothing.
/// </summary>
public sealed class AssetUploadCleanupService(
    IAssetStorage storage,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<AssetUploadCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
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
