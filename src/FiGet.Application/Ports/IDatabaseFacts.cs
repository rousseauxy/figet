namespace FiGet.Application.Ports;

/// <summary>Which engine holds the data. The two FiGet supports answer different questions about themselves.</summary>
public enum DatabaseEngine
{
    Sqlite,
    SqlServer,
}

/// <param name="EngineVersion">The engine's own version, as it reports it.</param>
/// <param name="Location">
/// Where the data is, already safe to show: a file path, or "host / catalog". Built from the connection string behind
/// this port and never carried across it, so no page can print a credential it was never given.
/// </param>
/// <param name="SizeBytes">Everything the database occupies, including SQLite's write-ahead log.</param>
/// <param name="ReclaimableBytes">Space already inside that which the engine would reuse before growing.</param>
/// <param name="LogSizeBytes">The transaction log, where the engine keeps one apart from the data. Zero otherwise.</param>
/// <param name="Mode">SQLite's journal mode, or SQL Server's recovery model: both decide how the log behaves.</param>
/// <param name="AppliedMigrations">In order, so the last is the schema this database is on.</param>
/// <param name="PendingMigrations">Migrations this build carries that the database has not had. Normally none.</param>
public sealed record DatabaseFacts(
    DatabaseEngine Engine,
    string EngineVersion,
    string Location,
    long SizeBytes,
    long ReclaimableBytes,
    long LogSizeBytes,
    string Mode,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations);

/// <summary>
/// What the database says about itself: how large, how current, and where. Read on demand for one admin page, never on
/// a path a client can reach - every answer here is a round trip nobody's package download should wait for.
/// </summary>
public interface IDatabaseFacts
{
    Task<DatabaseFacts> ReadAsync(CancellationToken cancellationToken);
}
