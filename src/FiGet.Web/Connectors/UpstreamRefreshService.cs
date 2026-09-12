using System.Collections.Concurrent;
using System.Threading.Channels;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Web.Connectors;

/// <summary>
/// The queue a request drops a refresh into and walks away from.
///
/// Unbounded on purpose: the only producer is a page that has already answered, the only consumer is one
/// background loop, and the in-flight set below bounds what can actually be queued to the number of
/// distinct packages being looked at. Bounding it would mean choosing between blocking a request that is
/// already finished and dropping a refresh silently - and the cap would never be reached anyway.
/// </summary>
public sealed class UpstreamRefreshQueue : IUpstreamRefreshQueue
{
    private readonly Channel<(FeedUpstream Upstream, string IdLower)> queue =
        Channel.CreateUnbounded<(FeedUpstream, string)>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// What is queued or being fetched. Twenty readers opening the same package on a stale catalogue
    /// would otherwise queue twenty identical walks of several megabytes each.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);

    public ChannelReader<(FeedUpstream Upstream, string IdLower)> Reader => queue.Reader;

    public void Enqueue(FeedUpstream upstream, string idLower)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        if (!inFlight.TryAdd(Key(upstream.Key, idLower), 0))
        {
            return;
        }

        if (!queue.Writer.TryWrite((upstream, idLower)))
        {
            // Unreachable for an unbounded channel short of shutdown, and even then the answer is the
            // same: the reader already has a page, and the next one refreshes it.
            Done(upstream.Key, idLower);
        }
    }

    /// <summary>Called by the worker when a refresh has finished, successfully or not.</summary>
    public void Done(int upstreamKey, string idLower) => inFlight.TryRemove(Key(upstreamKey, idLower), out _);

    private static string Key(int upstreamKey, string idLower) => upstreamKey + "|" + idLower;
}

/// <summary>
/// Fetches the catalogues that requests asked for and did not wait for.
///
/// One at a time, deliberately. These are the heaviest calls this server makes - a paged walk of several
/// megabytes against someone else's gallery - and the reason they are out here is that nobody is waiting.
/// Doing several at once would spend the upstream's patience to save time nobody is counting.
/// </summary>
public sealed class UpstreamRefreshService(
    UpstreamRefreshQueue queue,
    IUpstreamClient client,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<UpstreamRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (upstream, idLower) in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RefreshAsync(upstream, idLower, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nothing is waiting on this, so a failure must not take the loop down with it: the
                // cached catalogue stays exactly as it was and the next reader asks again.
                logger.LogWarning(ex, "Refreshing {Id} from upstream {Upstream} failed.", idLower, upstream.Name);
            }
            finally
            {
                queue.Done(upstream.Key, idLower);
            }
        }
    }

    private async Task RefreshAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        // Its own scope: the request that asked for this has long since finished, and its database
        // context with it.
        await using var scope = scopes.CreateAsyncScope();
        var index = scope.ServiceProvider.GetRequiredService<IUpstreamIndexStore>();

        var catalog = await client.GetCatalogAsync(upstream, idLower, cancellationToken);
        await index.SaveAsync(upstream.Key, idLower, catalog, stale: false, time.GetUtcNow().UtcDateTime, cancellationToken);

        logger.LogInformation(
            "Refreshed {Id} from upstream {Upstream}: {Versions} version(s).",
            idLower,
            upstream.Name,
            catalog.Versions.Count);
    }
}
