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

    /// <summary>
    /// One round of a job: takes the lease, runs, and records what the run said - or that it failed, which is logged and
    /// does not end the job. False when another instance held the lease, so nothing ran.
    /// </summary>
    public static async Task<bool> RunAsync(
        IServiceScopeFactory scopes,
        TimeProvider time,
        string job,
        TimeSpan interval,
        ILogger logger,
        Func<CancellationToken, Task<string>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!await TakeAsync(scopes, time, job, interval, logger, cancellationToken))
        {
            return false;
        }

        string result;
        try
        {
            result = await run(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The job {Job} failed; trying again at its next round.", job);
            result = Failed(ex);
        }

        await FinishAsync(scopes, time, job, result, logger, cancellationToken);
        return true;
    }

    /// <summary>
    /// Records how a run this instance started ended, for the jobs table. Never fails the job: a database that cannot be
    /// asked leaves the previous result showing, and the run itself already logged what it did.
    /// </summary>
    public static async Task FinishAsync(
        IServiceScopeFactory scopes,
        TimeProvider time,
        string job,
        string result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var holder = scope.ServiceProvider.GetRequiredService<JobInstance>().Id;
            await scope.ServiceProvider.GetRequiredService<IJobLeaseStore>()
                .RecordFinishedAsync(job, holder, time.GetUtcNow().UtcDateTime, result, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record how {Job} ended.", job);
        }
    }

    /// <summary>The one line a failed run leaves in the jobs table.</summary>
    public static string Failed(Exception ex) => "Failed: " + (ex ?? throw new ArgumentNullException(nameof(ex))).Message;
}
