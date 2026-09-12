using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

/// <summary>Settings in the database, so every replica agrees and a restart keeps the choice.</summary>
public sealed class EfSettingStore(FiGetDbContext db) : ISettingStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var row = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == Normalize(key), cancellationToken);
        return row?.Value;
    }

    public async Task SetAsync(string key, string value, string? updatedBy, CancellationToken cancellationToken)
    {
        var normalized = Normalize(key);

        // FindAsync rather than a query: it checks the change tracker first, so a set that follows a get
        // in the same scope updates the tracked row instead of trying to insert a second one.
        var row = await db.Settings.FindAsync([normalized], cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new Setting
            {
                Key = normalized,
                Value = value,
                UpdatedUtc = DateTime.UtcNow,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedUtc = DateTime.UtcNow;
            row.UpdatedBy = updatedBy;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Normalize(string key) => key.Trim().ToLowerInvariant();
}
