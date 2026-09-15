using FiGet.Infrastructure.Persistence;
using FiGet.Infrastructure.Sqlite;
using FiGet.Infrastructure.SqlServer;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Integration.Tests;

/// <summary>
/// The model and each provider's migrations agree: a model change without its migration fails here, before a push,
/// instead of at the CI step that runs <c>dotnet ef migrations has-pending-model-changes</c> - or at a start that refuses
/// the database. Both checks read the compiled model and the migration snapshot only; no database is opened.
/// </summary>
public sealed class MigrationDriftTests
{
    [Fact]
    public void The_sqlite_migrations_match_the_model()
    {
        var options = new DbContextOptionsBuilder<FiGetDbContext>();
        options.UseFiGetSqlite("Data Source=:memory:");
        using var context = new FiGetDbContext(options.Options);
        Assert.False(context.Database.HasPendingModelChanges(), "The model has changes without a SQLite migration: add one to FiGet.Infrastructure.Sqlite.");
    }

    [Fact]
    public void The_sql_server_migrations_match_the_model()
    {
        var options = new DbContextOptionsBuilder<FiGetDbContext>();
        options.UseFiGetSqlServer("Server=unused;Database=unused;TrustServerCertificate=True");
        using var context = new FiGetDbContext(options.Options);
        Assert.False(context.Database.HasPendingModelChanges(), "The model has changes without a SQL Server migration: add one to FiGet.Infrastructure.SqlServer.");
    }
}
