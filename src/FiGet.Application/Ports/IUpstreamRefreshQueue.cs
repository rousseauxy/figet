using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>
/// Asks for one upstream catalogue to be fetched again out of band. The request that asks does not wait:
/// it has already answered from what was cached, and the point of asking is that the next reader gets a
/// fresher answer without anyone having queued behind the walk.
///
/// Implementations must be safe to call from many requests at once and must collapse duplicates - twenty
/// readers opening the same package should cause one walk, not twenty.
/// </summary>
public interface IUpstreamRefreshQueue
{
    /// <summary>
    /// Queues a refresh, or does nothing when one for the same upstream and id is already in flight.
    /// Never throws and never blocks: a refresh that cannot be queued is a slightly staler page, which is
    /// not worth failing a request that has already succeeded.
    /// </summary>
    void Enqueue(FeedUpstream upstream, string idLower);
}
