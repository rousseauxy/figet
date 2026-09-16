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
}
