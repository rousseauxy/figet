using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.Configuration;
using FiGet.Web.Connectors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FiGet.Integration.Tests;

/// <summary>Every job runs on a documented interval, and a zero one means the job is not there at all.</summary>
public sealed class JobIntervalTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Fact]
    public void The_defaults_are_what_the_jobs_did_before_they_were_settings()
    {
        var jobs = server.Services.GetRequiredService<IOptions<FiGetOptions>>().Value.Jobs;

        Assert.Equal(TimeSpan.FromHours(1), jobs.Retention);
        Assert.Equal(TimeSpan.FromHours(6), jobs.AuditPrune);
        Assert.Equal(TimeSpan.FromHours(1), jobs.UploadSweep);
        Assert.Equal(TimeSpan.FromDays(1), jobs.UsagePrune);
    }

    [Fact]
    public void A_job_with_an_interval_is_registered()
    {
        Assert.Contains(server.Services.GetServices<IHostedService>(), s => s is RetentionJobService);
        Assert.Contains(server.Services.GetServices<IHostedService>(), s => s is AssetUploadCleanupService);
    }
}

/// <summary>A server with the pruning jobs switched off, which is what an operator running them elsewhere does.</summary>
public sealed class JobsOffServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Jobs:Retention", "00:00:00");
        builder.UseSetting("FiGet:Jobs:UploadSweep", "00:00:00");
    }
}

public sealed class JobsOffTests(JobsOffServerFixture server) : IClassFixture<JobsOffServerFixture>
{
    /// <summary>
    /// Not registered, rather than registered and idle: a job that ticks doing nothing still takes a lease, still
    /// logs, and still has to be reasoned about when something goes wrong at three in the morning.
    /// </summary>
    [Fact]
    public void A_job_switched_off_is_not_registered_at_all()
    {
        var hosted = server.Services.GetServices<IHostedService>().ToList();

        Assert.DoesNotContain(hosted, s => s is RetentionJobService);
        Assert.DoesNotContain(hosted, s => s is AssetUploadCleanupService);

        // The ones nobody switched off are still there, so the test cannot pass by the host having no jobs at all.
        Assert.Contains(hosted, s => s is UpstreamRefreshService);
    }

    /// <summary>The server still works with them off; they are a schedule, not a dependency.</summary>
    [Fact]
    public async Task The_server_still_serves_with_jobs_switched_off()
    {
        using var client = server.CreateClient();

        HttpAssert.Status(System.Net.HttpStatusCode.OK, await client.GetAsync("nuget/public/v3/index.json"));
    }
}
