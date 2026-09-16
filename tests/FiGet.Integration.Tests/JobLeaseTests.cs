using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteJobLeaseTests(SqliteServerFixture fixture) : JobLeaseTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerJobLeaseTests(SqlServerServerFixture fixture) : JobLeaseTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// One instance runs each periodic job. Added for the multi-replica deployment the 2026-09-14 review assessed: every replica
/// ran retention, the audit prune and the upload sweep on its own clock.
/// </summary>
public abstract class JobLeaseTests
{
    private readonly FiGetServerFixture server;

    protected JobLeaseTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task One_instance_holds_a_job_until_its_lease_runs_out()
    {
        var job = "test-" + Guid.NewGuid().ToString("N")[..8];
        var now = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        var hour = TimeSpan.FromHours(1);

        Assert.True(await TryAsync(job, "replica-a", now, now + hour));
        Assert.False(await TryAsync(job, "replica-b", now.AddMinutes(1), now.AddMinutes(1) + hour));

        // The holder renews its own lease at its next run.
        Assert.True(await TryAsync(job, "replica-a", now.AddMinutes(59), now.AddMinutes(59) + hour));
        Assert.False(await TryAsync(job, "replica-b", now.AddMinutes(61), now.AddMinutes(61) + hour));

        // A holder that stopped is taken over once its lease has run out.
        Assert.True(await TryAsync(job, "replica-b", now.AddMinutes(120), now.AddMinutes(120) + hour));
        Assert.False(await TryAsync(job, "replica-a", now.AddMinutes(121), now.AddMinutes(121) + hour));
    }

    /// <summary>Instances asking for a job nobody has held yet, all at once: exactly one gets it.</summary>
    [Fact]
    public async Task Instances_asking_at_once_for_a_new_job_get_it_once()
    {
        var job = "race-" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => TryAsync(job, "replica-" + i, now, now.AddHours(1))));
        Assert.Single(results, taken => taken);
    }

    /// <summary>
    /// When each job last started, which is what the database page reports. Every take writes it, including a renewal
    /// and a takeover, so "last started" cannot silently freeze at the first run.
    /// </summary>
    [Fact]
    public async Task Taking_a_job_records_when_it_started()
    {
        var job = "taken-" + Guid.NewGuid().ToString("N")[..8];
        var now = new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc);
        var hour = TimeSpan.FromHours(1);

        Assert.Null(await TakenAsync(job));

        Assert.True(await TryAsync(job, "replica-a", now, now + hour));
        Assert.Equal(now, await TakenAsync(job));

        // A renewal moves it: otherwise a job running every hour would report the day it first ran.
        Assert.True(await TryAsync(job, "replica-a", now.AddMinutes(59), now.AddMinutes(59) + hour));
        Assert.Equal(now.AddMinutes(59), await TakenAsync(job));

        // A replica that could not take the job leaves the time alone.
        Assert.False(await TryAsync(job, "replica-b", now.AddMinutes(61), now.AddMinutes(61) + hour));
        Assert.Equal(now.AddMinutes(59), await TakenAsync(job));

        // And a takeover after the lease ran out records the new holder's run.
        Assert.True(await TryAsync(job, "replica-b", now.AddMinutes(180), now.AddMinutes(180) + hour));
        Assert.Equal(now.AddMinutes(180), await TakenAsync(job));
    }

    private async Task<DateTime?> TakenAsync(string job)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IJobLeaseStore>().ListAsync(TestContext.Current.CancellationToken);
        return leases.FirstOrDefault(l => l.Name == job)?.TakenUtc;
    }

    private async Task<bool> TryAsync(string job, string holder, DateTime now, DateTime expires)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobLeaseStore>().TryAcquireAsync(job, holder, now, expires, CancellationToken.None);
    }
}
