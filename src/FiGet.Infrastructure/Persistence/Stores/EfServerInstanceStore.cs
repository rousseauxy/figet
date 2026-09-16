using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

/// <summary>
/// One row per running copy, updated in place. An update that matches nothing is this instance's first heartbeat, so the
/// row is created then; a second instance cannot collide, because the id carries a value of its own start.
/// </summary>
public sealed class EfServerInstanceStore(FiGetDbContext db) : IServerInstanceStore
{
    public async Task HeartbeatAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var updated = await db.ServerInstances
            .Where(i => i.Id == instance.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(i => i.LastSeenUtc, instance.LastSeenUtc).SetProperty(i => i.Version, instance.Version),
                cancellationToken);
        if (updated > 0)
        {
            return;
        }

        db.ServerInstances.Add(instance);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two heartbeats of this instance at once, which is not worth a retry: the next minute writes it.
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<ServerInstance>> ListAsync(CancellationToken cancellationToken) =>
        await db.ServerInstances.AsNoTracking().OrderByDescending(i => i.LastSeenUtc).ToListAsync(cancellationToken);

    public Task<int> PruneAsync(DateTime beforeUtc, CancellationToken cancellationToken) =>
        db.ServerInstances.Where(i => i.LastSeenUtc < beforeUtc).ExecuteDeleteAsync(cancellationToken);

    public Task ForgetAsync(string id, CancellationToken cancellationToken) =>
        db.ServerInstances.Where(i => i.Id == id).ExecuteDeleteAsync(cancellationToken);
}
