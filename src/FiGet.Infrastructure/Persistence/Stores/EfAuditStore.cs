using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfAuditStore(FiGetDbContext db) : IAuditStore
{
    public async Task AddRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        db.AuditEntries.AddRange(entries);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim().ToLowerInvariant();
            entries = action.EndsWith('.')
                ? entries.Where(e => e.Action.StartsWith(action))
                : entries.Where(e => e.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            // Stored as "user:name" or "token:name"; a filter of just the name finds either, and the kind narrows it.
            var actor = query.Actor.Trim().ToLowerInvariant();
            entries = actor.Contains(':', StringComparison.Ordinal)
                ? entries.Where(e => e.ActorLower == actor)
                : entries.Where(e => e.ActorLower == "user:" + actor || e.ActorLower == "token:" + actor || e.ActorLower == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.Feed))
        {
            var feed = query.Feed.Trim().ToLowerInvariant();
            entries = entries.Where(e => e.FeedLower == feed);
        }

        if (query.FromUtc is { } from)
        {
            entries = entries.Where(e => e.WhenUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            entries = entries.Where(e => e.WhenUtc < to);
        }

        if (query.BeforeKey is { } before)
        {
            entries = entries.Where(e => e.Key < before);
        }

        return await entries
            .OrderByDescending(e => e.Key)
            .Take(Math.Clamp(query.Take, 1, 500))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken) =>
        await db.AuditEntries.AsNoTracking().Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(cancellationToken);

    public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken) =>
        db.AuditEntries.Where(e => e.WhenUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
}
