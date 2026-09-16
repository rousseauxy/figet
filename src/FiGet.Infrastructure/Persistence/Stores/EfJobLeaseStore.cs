using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

/// <summary>
/// A compare-and-set on one row, which both providers make atomic: take it when it is free, run out or already ours;
/// otherwise create it, and let the primary key refuse the second of two instances that tried at once.
/// </summary>
public sealed class EfJobLeaseStore(FiGetDbContext db) : IJobLeaseStore
{
    public async Task<bool> TryAcquireAsync(string name, string holder, DateTime nowUtc, DateTime expiresUtc, CancellationToken cancellationToken)
    {
        var taken = await db.JobLeases
            .Where(l => l.Name == name && (l.ExpiresUtc <= nowUtc || l.Holder == holder))
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.Holder, holder).SetProperty(l => l.ExpiresUtc, expiresUtc).SetProperty(l => l.TakenUtc, nowUtc),
                cancellationToken);
        if (taken > 0)
        {
            return true;
        }

        if (await db.JobLeases.AsNoTracking().AnyAsync(l => l.Name == name, cancellationToken))
        {
            return false;
        }

        db.JobLeases.Add(new JobLease { Name = name, Holder = holder, ExpiresUtc = expiresUtc, TakenUtc = nowUtc });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another instance created it a moment ago; it holds the job.
            return false;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<JobLeaseState>> ListAsync(CancellationToken cancellationToken) =>
        await db.JobLeases
            .AsNoTracking()
            .OrderBy(l => l.Name)
            .Select(l => new JobLeaseState(l.Name, l.Holder, l.ExpiresUtc, l.TakenUtc))
            .ToListAsync(cancellationToken);
}
