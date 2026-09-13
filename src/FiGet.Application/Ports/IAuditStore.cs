using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>
/// A page of the audit log, newest first. Every filter is optional: <see cref="Action"/> matches a whole verb or a prefix
/// ending in a dot (<c>signin.</c>), <see cref="Actor"/> and <see cref="Feed"/> are case-insensitive exact matches after
/// the <c>user:</c> or <c>token:</c> kind, and <see cref="BeforeKey"/> continues from the last entry of the previous page.
/// </summary>
public sealed record AuditQuery(
    string? Action = null,
    string? Actor = null,
    string? Feed = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    long? BeforeKey = null,
    int Take = 100);

public interface IAuditStore
{
    Task AddRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);

    /// <summary>The distinct verbs recorded so far, for the page's filter.</summary>
    Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken);

    /// <summary>Deletes entries older than <paramref name="cutoffUtc"/>; returns how many.</summary>
    Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken);
}
