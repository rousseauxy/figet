namespace FiGet.Application.Ports;

public interface IJobLeaseStore
{
    /// <summary>
    /// Takes the job for <paramref name="holder"/> until <paramref name="expiresUtc"/>: when nobody holds it, when the
    /// holder's lease has run out, or to renew its own. False while another instance holds it.
    /// </summary>
    Task<bool> TryAcquireAsync(string name, string holder, DateTime nowUtc, DateTime expiresUtc, CancellationToken cancellationToken);
}

/// <summary>This process's name in the lease table: the machine - a pod's name - and a value of its own start.</summary>
public sealed class JobInstance
{
    public string Id { get; } = Environment.MachineName + ":" + Guid.NewGuid().ToString("N")[..8];
}
