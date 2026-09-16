using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteServerInstanceTests(SqliteServerFixture fixture) : ServerInstanceTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerServerInstanceTests(SqlServerServerFixture fixture) : ServerInstanceTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Each running copy saying it is there. Built for the cluster deployment, where four pods share one database and
/// nothing until now could say whether all four were up or what each was running.
/// </summary>
public abstract class ServerInstanceTests
{
    private readonly FiGetServerFixture server;

    protected ServerInstanceTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    /// <summary>
    /// The instance serving these tests writes its own row at start, without being asked. Everything else here is about
    /// several of them, which one process cannot be - so the others are written directly, as replicas would.
    /// </summary>
    [Fact]
    public async Task This_instance_says_it_is_running()
    {
        var self = server.Services.GetRequiredService<JobInstance>();

        var instances = await ListAsync();

        var mine = Assert.Single(instances, i => i.Id == self.Id);
        Assert.Equal(Environment.MachineName, mine.Machine);
        Assert.NotEqual(default, mine.StartedUtc);
        Assert.True(mine.LastSeenUtc >= mine.StartedUtc, "An instance cannot last have been seen before it started.");
    }

    /// <summary>A heartbeat updates the row rather than adding one, or a pod would be a row a minute.</summary>
    [Fact]
    public async Task A_replica_keeps_one_row_however_often_it_says_so()
    {
        var id = "pod-" + Guid.NewGuid().ToString("N")[..8];
        var started = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc);

        await BeatAsync(id, started, started, "1.1.0");
        await BeatAsync(id, started, started.AddMinutes(1), "1.1.0");
        await BeatAsync(id, started, started.AddMinutes(2), "1.2.0");

        var row = Assert.Single(await ListAsync(), i => i.Id == id);
        Assert.Equal(started.AddMinutes(2), row.LastSeenUtc);
        Assert.Equal(started, row.StartedUtc);

        // The version moves with the instance: an upgraded pod reporting its old version would be worse than silence.
        Assert.Equal("1.2.0", row.Version);
    }

    /// <summary>
    /// A cluster that redeploys daily makes a new instance id every time. Without a sweep the list would grow for ever
    /// and stop being readable long before it became large.
    /// </summary>
    [Fact]
    public async Task An_instance_that_has_been_quiet_for_long_enough_is_forgotten()
    {
        var gone = "gone-" + Guid.NewGuid().ToString("N")[..8];
        var here = "here-" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;

        await BeatAsync(gone, now.AddDays(-9), now.AddDays(-8), "1.1.0");
        await BeatAsync(here, now.AddMinutes(-5), now, "1.2.0");

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var removed = await scope.ServiceProvider.GetRequiredService<IServerInstanceStore>()
                .PruneAsync(now.AddDays(-2), TestContext.Current.CancellationToken);
            Assert.True(removed >= 1, "The instance quiet for eight days was not removed.");
        }

        var instances = await ListAsync();
        Assert.DoesNotContain(instances, i => i.Id == gone);
        Assert.Contains(instances, i => i.Id == here);
    }

    private async Task BeatAsync(string id, DateTime started, DateTime seen, string version)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IServerInstanceStore>().HeartbeatAsync(
            new ServerInstance { Id = id, Machine = id, Version = version, StartedUtc = started, LastSeenUtc = seen },
            TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<ServerInstance>> ListAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServerInstanceStore>().ListAsync(TestContext.Current.CancellationToken);
    }
}
