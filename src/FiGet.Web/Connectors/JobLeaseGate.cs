using FiGet.Application.Ports;

namespace FiGet.Web.Connectors;

/// <summary>
/// Whether this instance runs a periodic job this time. The lease lasts one interval, so the instance that holds it renews
/// it at its next run, and another takes over one interval after the holder stopped. A database that cannot be asked
/// means the job is skipped, never run by everyone.
/// </summary>
public static class JobLeaseGate
{
    public static async Task<bool> TakeAsync(
        IServiceScopeFactory scopes,
        TimeProvider time,
        string job,
        TimeSpan interval,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var now = time.GetUtcNow().UtcDateTime;
            var holder = scope.ServiceProvider.GetRequiredService<JobInstance>().Id;

            // A little short of the interval, so the holder's own next run, which the timer may start a moment early, renews it.
            return await scope.ServiceProvider.GetRequiredService<IJobLeaseStore>()
                .TryAcquireAsync(job, holder, now, now + interval - TimeSpan.FromMinutes(1), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not take the lease for {Job}; skipping this run.", job);
            return false;
        }
    }
}
