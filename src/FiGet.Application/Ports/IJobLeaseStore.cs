namespace FiGet.Application.Ports;

public interface IJobLeaseStore
{
    /// <summary>
    /// Takes the job for <paramref name="holder"/> until <paramref name="expiresUtc"/>: when nobody holds it, when the
    /// holder's lease has run out, or to renew its own. False while another instance holds it.
    /// </summary>
    Task<bool> TryAcquireAsync(string name, string holder, DateTime nowUtc, DateTime expiresUtc, CancellationToken cancellationToken);

    /// <summary>Every lease, for a page that reports when each job last started. At most one row per job.</summary>
    Task<IReadOnlyList<JobLeaseState>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>One job's lease, as a reader sees it.</summary>
/// <param name="TakenUtc">When the job last started, or null when it has not run against this database.</param>
public sealed record JobLeaseState(string Name, string Holder, DateTime ExpiresUtc, DateTime? TakenUtc);

/// <summary>This process's name in the lease table: the machine - a pod's name - and a value of its own start.</summary>
public sealed class JobInstance
{
    public string Id { get; } = Environment.MachineName + ":" + Guid.NewGuid().ToString("N")[..8];
}
