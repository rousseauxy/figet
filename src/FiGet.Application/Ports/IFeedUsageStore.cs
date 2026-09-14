using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>A number of uses of one feed in one hour.</summary>
public sealed record FeedUsageCount(int FeedKey, DateTime HourUtc, FeedUsageKind Kind, long Count);

public interface IFeedUsageStore
{
    /// <summary>Adds the counts to what is stored, in the database, so replicas writing the same hour add up.</summary>
    Task AddAsync(IReadOnlyCollection<FeedUsageCount> counts, CancellationToken cancellationToken);

    /// <summary>The stored hours of one kind for these feeds, from the hour given.</summary>
    Task<IReadOnlyList<FeedUsageCount>> ListAsync(IReadOnlyCollection<int> feedKeys, FeedUsageKind kind, DateTime fromUtc, CancellationToken cancellationToken);

    /// <summary>Removes the hours before the one given. Returns how many rows went.</summary>
    Task<int> PruneAsync(DateTime beforeUtc, CancellationToken cancellationToken);
}
