using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Says that this copy is running, once a minute, so a deployment of several can be seen as a whole. Unlike every other
/// periodic job this takes no lease: the point is that each replica writes, and a lease would leave the others invisible.
///
/// A row is a report and never a permission. Nothing reads these to decide anything, so a stale row - a pod killed
/// between heartbeats - costs a line on a page and not a job that stops running.
/// </summary>
public sealed class InstanceHeartbeatService(
    JobInstance instance,
    IServiceScopeFactory scopes,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<InstanceHeartbeatService> logger) : BackgroundService
{
    /// <summary>Not a setting: this is how an instance says it exists, and a longer one only makes the page staler.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a silent instance is still listed. Long enough that a page opened after a rollout still shows what went
    /// away, short enough that a cluster redeploying daily does not collect a row per pod for ever.
    /// </summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromDays(2);

    private readonly DateTime startedUtc = time.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // At once, not after the first minute: a page opened right after a start should already show this instance.
        await BeatAsync(stoppingToken);
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await BeatAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IServerInstanceStore>();
            var now = time.GetUtcNow().UtcDateTime;
            await store.HeartbeatAsync(
                new ServerInstance
                {
                    Id = instance.Id,
                    Machine = Environment.MachineName,
                    Version = ServerVersion.Resolve(options.Value.Version),
                    StartedUtc = startedUtc,
                    LastSeenUtc = now,
                },
                cancellationToken);

            // Any instance may sweep: there is nothing to coordinate, and the rows removed are ones nobody is writing.
            await store.PruneAsync(now - Forgotten, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A heartbeat is a report. Failing to write one must never be the reason a server stops serving packages.
            logger.LogDebug(ex, "This instance could not record that it is running; trying again next minute.");
        }
    }
}
