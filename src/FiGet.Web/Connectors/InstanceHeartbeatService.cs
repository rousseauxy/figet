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
    IServiceScopeFactory scopes,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<InstanceHeartbeatService> logger) : BackgroundService
{
    /// <summary>Not a setting: this is how an instance says it exists, and a longer one only makes the page staler.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an instance that never said goodbye is still listed. It is the trace of something that went away
    /// without being asked, so it is worth keeping past the moment it happened - but not past the day.
    /// </summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromDays(1);

    /// <summary>
    /// Missed heartbeats before an instance stops counting as running. Three, so one slow minute is not an alarm.
    /// Shared with the page, which must not draw its own line.
    /// </summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(3);

    private readonly DateTime startedUtc = time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// What this copy is called in the list, which is a place and not a process: a container that restarts under the
    /// same name takes its own row back instead of adding one. The page asks the same way, so the two cannot disagree.
    /// </summary>
    public static string NameOf(FiGetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.InstanceName) ? Environment.MachineName : options.InstanceName.Trim();
    }

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

        await ForgetAsync();
    }

    /// <summary>
    /// Says goodbye. Without this every restart and every rolling update leaves a row that looks like a copy still
    /// running, for as long as it takes to go quiet - so an ordinary deployment would raise the very warnings this page
    /// exists to make meaningful. A stop that skips this (a crash, a killed pod) is exactly what should leave a trace.
    /// </summary>
    private async Task ForgetAsync()
    {
        try
        {
            // The stopping token is already cancelled here, so this gets its own short one rather than none at all: a
            // shutdown must not hang on a database that is not answering.
            using var leaving = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IServerInstanceStore>().ForgetAsync(NameOf(options.Value), leaving.Token);
        }
        catch (Exception ex)
        {
            // Then the row stays, and the page will call this instance quiet in a few minutes. That is the honest
            // outcome of a stop that could not reach the database, and never a reason to fail a shutdown.
            logger.LogDebug(ex, "This instance could not remove its row on shutdown; it will be listed as quiet instead.");
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
                    Id = NameOf(options.Value),
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
