using System.Data.Common;
using System.Globalization;
using FiGet.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence;

/// <summary>
/// Asks the database about itself, in whichever dialect it speaks.
///
/// The provider is chosen by name rather than by <c>IsSqlite()</c>/<c>IsSqlServer()</c>: those extension methods live in
/// the provider packages, and this project deliberately references only EF Core's relational parts, so that a new
/// provider never becomes a reference the whole of Infrastructure carries.
///
/// Nothing here counts rows. Every figure is a page count, a file size or a migration list, which is what makes a page
/// an administrator may refresh at will cost about the same as a health check.
/// </summary>
public sealed class DatabaseFactsReader(FiGetDbContext db) : IDatabaseFacts
{
    public async Task<DatabaseFacts> ReadAsync(CancellationToken cancellationToken)
    {
        var engine = (db.Database.ProviderName ?? "").Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? DatabaseEngine.SqlServer
            : DatabaseEngine.Sqlite;

        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var connectionString = db.Database.GetConnectionString() ?? "";

        return engine == DatabaseEngine.SqlServer
            ? await ReadSqlServerAsync(connectionString, applied, pending, cancellationToken)
            : await ReadSqliteAsync(connectionString, applied, pending, cancellationToken);
    }

    /// <summary>
    /// SQLite answers in pages. The write-ahead log is added to the total on purpose: it is a second file, it can be
    /// larger than the database on a busy instance, and a volume fills up with the sum and not with the pragma.
    /// </summary>
    private async Task<DatabaseFacts> ReadSqliteAsync(
        string connectionString,
        IReadOnlyList<string> applied,
        IReadOnlyList<string> pending,
        CancellationToken cancellationToken)
    {
        var version = await ScalarTextAsync("select sqlite_version() as \"Value\"", cancellationToken);
        var pageSize = await ScalarNumberAsync("select page_size as \"Value\" from pragma_page_size()", cancellationToken);
        var pages = await ScalarNumberAsync("select page_count as \"Value\" from pragma_page_count()", cancellationToken);
        var free = await ScalarNumberAsync("select freelist_count as \"Value\" from pragma_freelist_count()", cancellationToken);
        var journal = await ScalarTextAsync("select journal_mode as \"Value\" from pragma_journal_mode()", cancellationToken);

        var path = ConnectionTarget.SqliteFile(connectionString);
        return new DatabaseFacts(
            DatabaseEngine.Sqlite,
            version,
            path.Length == 0 ? "in memory" : path,
            (pages * pageSize) + SideFileBytes(path),
            free * pageSize,
            LogSizeBytes: 0,
            journal.ToUpperInvariant(),
            applied,
            pending);
    }

    /// <summary>
    /// SQL Server keeps its log apart from its data, and the two answer different questions: a log that dwarfs the data
    /// is the full recovery model without log backups, not a database that grew.
    /// </summary>
    private async Task<DatabaseFacts> ReadSqlServerAsync(
        string connectionString,
        IReadOnlyList<string> applied,
        IReadOnlyList<string> pending,
        CancellationToken cancellationToken)
    {
        var version = await ScalarTextAsync("select cast(serverproperty('ProductVersion') as nvarchar(128)) as [Value]", cancellationToken);
        var edition = await ScalarTextAsync("select cast(serverproperty('Edition') as nvarchar(128)) as [Value]", cancellationToken);
        var recovery = await ScalarTextAsync("select cast(databasepropertyex(db_name(), 'Recovery') as nvarchar(60)) as [Value]", cancellationToken);

        // Pages are 8 KB in every supported version; FILEPROPERTY answers for the current database only, which is the one
        // this connection is on.
        var data = await ScalarNumberAsync(
            "select isnull(sum(cast(size as bigint)), 0) * 8192 as [Value] from sys.database_files where type_desc = 'ROWS'",
            cancellationToken);
        var used = await ScalarNumberAsync(
            "select isnull(sum(cast(fileproperty(name, 'SpaceUsed') as bigint)), 0) * 8192 as [Value] from sys.database_files where type_desc = 'ROWS'",
            cancellationToken);
        var log = await ScalarNumberAsync(
            "select isnull(sum(cast(size as bigint)), 0) * 8192 as [Value] from sys.database_files where type_desc = 'LOG'",
            cancellationToken);

        return new DatabaseFacts(
            DatabaseEngine.SqlServer,
            edition.Length == 0 ? version : version + " (" + edition + ")",
            ConnectionTarget.SqlServerHostAndCatalog(connectionString),
            data + log,
            Math.Max(0, data - used),
            log,
            recovery.ToUpperInvariant(),
            applied,
            pending);
    }

    /// <summary>The write-ahead log and its index, which exist only while the database is open in WAL mode.</summary>
    private static long SideFileBytes(string path)
    {
        if (path.Length == 0)
        {
            return 0;
        }

        var total = 0L;
        foreach (var suffix in (string[])["-wal", "-shm"])
        {
            try
            {
                var file = new FileInfo(path + suffix);
                if (file.Exists)
                {
                    total += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The figure is a report, not a decision: a file we may not stat is left out rather than failing the page.
            }
        }

        return total;
    }

    private async Task<long> ScalarNumberAsync(string sql, CancellationToken cancellationToken) =>
        (await db.Database.SqlQueryRaw<long?>(sql).ToListAsync(cancellationToken)).FirstOrDefault() ?? 0;

    private async Task<string> ScalarTextAsync(string sql, CancellationToken cancellationToken) =>
        (await db.Database.SqlQueryRaw<string?>(sql).ToListAsync(cancellationToken)).FirstOrDefault() ?? "";
}

/// <summary>
/// Where a connection points, in the words a page may show.
///
/// Read with an allow-list, never by removing the password: a list of what to drop has to be right about every key a
/// provider will ever add, and is wrong the first time one is added. Anything not named here simply never leaves this
/// class, so a page cannot print a credential because it never receives one.
/// </summary>
public static class ConnectionTarget
{
    /// <summary>The database file a SQLite connection string names, as a full path. Empty when it names none.</summary>
    public static string SqliteFile(string connectionString)
    {
        var source = Value(connectionString, "Data Source", "DataSource", "Filename");
        if (source.Length == 0 || source.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(source);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return source;
        }
    }

    /// <summary>"host / catalog" for SQL Server: the two parts that say which database this is, and nothing else.</summary>
    public static string SqlServerHostAndCatalog(string connectionString)
    {
        var host = Value(connectionString, "Data Source", "Server", "Address", "Addr", "Network Address");
        var catalog = Value(connectionString, "Initial Catalog", "Database");
        return (host.Length, catalog.Length) switch
        {
            (0, 0) => "unknown",
            (_, 0) => host,
            (0, _) => catalog,
            _ => host + " / " + catalog,
        };
    }

    private static string Value(string connectionString, params string[] keys)
    {
        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            // Unparseable: say nothing rather than fall back to showing the string itself.
            return "";
        }

        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value is not null)
            {
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }

        return "";
    }
}
