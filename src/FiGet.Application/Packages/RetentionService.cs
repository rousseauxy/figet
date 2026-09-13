using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FiGet.Application.Packages;

/// <summary>What a retention run did. <see cref="FreedBytes"/> counts only what was deleted; an unlisted version keeps its file.</summary>
public sealed record RetentionReport(int Unlisted, int Deleted, int Pruned, long FreedBytes, bool StoppedAtLimit)
{
    public int Total => Unlisted + Deleted + Pruned;
}

/// <summary>Applies a feed's retention and cache pruning (<see cref="RetentionPolicy"/>): previewed on the feed page, run by a background job or a button.</summary>
public sealed class RetentionService(IPackageStore store, PackageIngestionService ingestion, TimeProvider time, ILogger<RetentionService> logger)
{
    /// <summary>
    /// The most one run removes. A first run on a large cache could otherwise hold the database for a long time; the next
    /// run carries on where this one stopped.
    /// </summary>
    public const int MaxRemovalsPerRun = 1000;

    public async Task<IReadOnlyList<RetentionRemoval>> PreviewAsync(Feed feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var rules = RetentionRules.Of(feed);
        return rules.Any && feed.Kind != FeedKind.Assets
            ? RetentionPolicy.Plan(await store.ListRetentionCandidatesAsync(feed.Key, cancellationToken), rules, feed.DeletionBehavior, time.GetUtcNow().UtcDateTime)
            : [];
    }

    public async Task<RetentionReport> RunAsync(Feed feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var plan = await PreviewAsync(feed, cancellationToken);
        int unlisted = 0, deleted = 0, pruned = 0;
        long freed = 0;
        foreach (var removal in plan.Take(MaxRemovalsPerRun))
        {
            var version = removal.Version;
            if (removal.Reason == RetentionReason.UnusedCache || feed.DeletionBehavior == PackageDeletionBehavior.HardDelete)
            {
                if (await ingestion.PurgeAsync(feed, version.Id, version.NormalizedVersion, cancellationToken))
                {
                    freed += version.Size;
                    if (removal.Reason == RetentionReason.UnusedCache)
                    {
                        pruned++;
                    }
                    else
                    {
                        deleted++;
                    }
                }
            }
            else if (await ingestion.UnlistAsync(feed, version.Id, version.NormalizedVersion, cancellationToken))
            {
                unlisted++;
            }
        }

        var report = new RetentionReport(unlisted, deleted, pruned, freed, plan.Count > MaxRemovalsPerRun);
        if (report.Total > 0)
        {
            logger.LogInformation(
                "Retention on feed {Feed}: {Unlisted} unlisted, {Deleted} deleted, {Pruned} cached copies pruned, {Bytes} bytes freed.",
                feed.Name,
                unlisted,
                deleted,
                pruned,
                freed);
        }

        return report;
    }
}
