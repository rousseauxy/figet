namespace FiGet.Application.Ports;

using FiGet.Domain.Entities;

/// <summary>
/// Where each running copy says it is there. Every replica writes its own row; anything that reads them is reporting,
/// never deciding.
/// </summary>
public interface IServerInstanceStore
{
    /// <summary>Records that this instance is running, creating its row the first time.</summary>
    Task HeartbeatAsync(ServerInstance instance, CancellationToken cancellationToken);

    /// <summary>Every instance that has said so and not yet been swept, the one seen most recently first.</summary>
    Task<IReadOnlyList<ServerInstance>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes instances not seen since <paramref name="beforeUtc"/>. Without this a cluster that redeploys daily would
    /// keep a row per pod for ever, and the list would stop being readable long before it became large.
    /// </summary>
    Task<int> PruneAsync(DateTime beforeUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one instance's row, which it does for itself as it stops. That is what makes a row that is still there
    /// worth reading: an instance that was asked to stop leaves no trace, so what remains is what went away without
    /// being asked - a crash, a killed pod, a lost node.
    /// </summary>
    Task ForgetAsync(string id, CancellationToken cancellationToken);
}
