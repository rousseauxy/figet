using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteDatabaseFactsTests(SqliteServerFixture fixture) : DatabaseFactsTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerDatabaseFactsTests(SqlServerServerFixture fixture) : DatabaseFactsTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// What the database says about itself, asked of both engines: the two dialects are the whole of this adapter, so a
/// test that ran on one of them would cover half of it.
/// </summary>
public abstract class DatabaseFactsTests
{
    private readonly FiGetServerFixture server;

    protected DatabaseFactsTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task The_engine_names_itself_and_says_how_large_it_is()
    {
        var facts = await ReadAsync();

        Assert.False(string.IsNullOrWhiteSpace(facts.EngineVersion), "The engine reports no version.");
        Assert.True(facts.SizeBytes > 0, $"A migrated database cannot be {facts.SizeBytes} bytes.");
        Assert.InRange(facts.ReclaimableBytes, 0, facts.SizeBytes);
        Assert.False(string.IsNullOrWhiteSpace(facts.Mode), "The engine reports no journal mode or recovery model.");
        Assert.NotEqual("unknown", facts.Location);
    }

    /// <summary>
    /// The schema the database is on, and nothing waiting. This also fails when a migration was added to one provider
    /// and not the other, which is the mistake two migration assemblies invite.
    /// </summary>
    [Fact]
    public async Task A_started_server_is_on_the_newest_migration_with_none_pending()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGet.Infrastructure.Persistence.FiGetDbContext>();
        var newest = db.Database.GetMigrations().Last();

        var facts = await ReadAsync();

        Assert.Empty(facts.PendingMigrations);
        Assert.NotEmpty(facts.AppliedMigrations);
        Assert.Equal(newest, facts.AppliedMigrations[^1]);
    }

    /// <summary>The engine each fixture actually runs, so the branch that was taken is the branch under test.</summary>
    [Fact]
    public async Task The_engine_reported_is_the_one_this_server_runs_on()
    {
        var facts = await ReadAsync();

        Assert.Equal(server.Database == TestDatabase.SqlServer ? DatabaseEngine.SqlServer : DatabaseEngine.Sqlite, facts.Engine);
        if (facts.Engine == DatabaseEngine.Sqlite)
        {
            // A file path, and the write-ahead log counted with it: the volume fills up with the sum.
            Assert.EndsWith(".db", facts.Location, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, facts.LogSizeBytes);
        }
        else
        {
            // SQL Server keeps its log apart, and it is never nothing.
            Assert.True(facts.LogSizeBytes > 0, "SQL Server reports no transaction log.");
            Assert.True(facts.SizeBytes > facts.LogSizeBytes, "The total should hold both the data and the log.");
        }
    }

    /// <summary>
    /// The page is refreshable by hand, so this must not be a table scan. Asserted as a ceiling generous enough to
    /// survive a slow machine and tight enough to catch someone adding a count.
    /// </summary>
    [Fact]
    public async Task Reading_the_facts_is_cheap()
    {
        await ReadAsync();

        var started = DateTime.UtcNow;
        await ReadAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "Reading the database's facts took over two seconds.");
    }

    private async Task<DatabaseFacts> ReadAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDatabaseFacts>().ReadAsync(TestContext.Current.CancellationToken);
    }
}
