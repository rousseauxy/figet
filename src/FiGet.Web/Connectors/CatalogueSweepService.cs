using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Refreshes the stored catalogue of every id each proxy feed holds, so a package nobody has browsed for a week is
/// still known to have moved. Without it an upstream's version list is only ever refreshed by somebody asking for it,
/// and the packages a change report most wants to talk about are exactly the quiet ones.
///
/// It cannot hammer a gallery, by four bounds, three of which it inherits:
/// it only ever enqueues ids the upstream already has a catalogue row for, so it can never introduce a new id;
/// <see cref="UpstreamRefreshQueue"/> collapses duplicates;
/// the refresh worker is single-threaded on purpose;
/// and a catalogue refreshed inside the interval is skipped, so on a busy feed this queues almost nothing.
/// </summary>
public sealed class CatalogueSweepService(
    IServiceScopeFactory scopes,
    IUpstreamRefreshQueue refreshes,
    AuditLog audit,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<CatalogueSweepService> logger) : BackgroundService
{
    /// <summary>Not at start-up: a restart loop must not become a fetching loop against somebody else's gallery.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    private TimeSpan Interval => options.Value.Jobs.CatalogueSweep;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                if (await JobLeaseGate.TakeAsync(scopes, time, JobLeaseNames.CatalogueSweep, Interval, logger, stoppingToken))
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
        var packages = scope.ServiceProvider.GetRequiredService<IPackageStore>();
        var index = scope.ServiceProvider.GetRequiredService<IUpstreamIndexStore>();
        var now = time.GetUtcNow().UtcDateTime;
        var cap = options.Value.Connector.SweepMaxIdsPerFeed;

        foreach (var feed in await feeds.ListAsync(cancellationToken))
        {
            if (feed.Kind == FeedKind.Assets || !feed.Upstreams.Any(u => u.Enabled))
            {
                continue;
            }

            var held = await packages.ListIdsAsync(feed.Key, cancellationToken);
            if (held.Count == 0)
            {
                continue;
            }

            var queued = 0;
            var remaining = new HashSet<string>(held, StringComparer.Ordinal);
            foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
            {
                var asked = remaining.Where(id => ConnectorService.Allows(upstream, id)).ToList();
                if (asked.Count == 0)
                {
                    continue;
                }

                foreach (var (idLower, cached) in await index.FindManyAsync(upstream.Key, asked, cancellationToken))
                {
                    // Ownership, as everywhere else: the first upstream that knows the id is the one asked about it.
                    remaining.Remove(idLower);
                    if (now - cached.FetchedUtc < Interval || queued >= cap)
                    {
                        continue;
                    }

                    refreshes.Enqueue(upstream, idLower);
                    queued++;
                }
            }

            if (queued == 0)
            {
                continue;
            }

            audit.Record(null, "catalogue.sweep", feed.Name, $"feed={feed.Name} held={held.Count} queued={queued}{(queued >= cap ? " more=next-run" : "")}");
            if (queued >= cap)
            {
                logger.LogWarning(
                    "The catalogue sweep of feed {Feed} stopped at {Cap} ids; the rest are refreshed on the next run. Raise FiGet:Connector:SweepMaxIdsPerFeed if this feed is meant to be this large.",
                    feed.Name,
                    cap);
            }
        }
    }
}
