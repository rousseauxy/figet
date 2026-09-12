namespace FiGet.Domain.Entities;

/// <summary>A package id within one feed. The id keeps the casing of its first publication.</summary>
public sealed class Package
{
    public long Key { get; set; }

    public int FeedKey { get; set; }

    public Feed? Feed { get; set; }

    public required string Id { get; set; }

    public required string IdLower { get; set; }

    public List<PackageVersion> Versions { get; set; } = [];
}
