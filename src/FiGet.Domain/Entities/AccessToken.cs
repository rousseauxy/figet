namespace FiGet.Domain.Entities;

/// <summary>
/// An API key: a service token, or a personal key that belongs to an account. Only the SHA-256 hash of the secret is stored; the secret is
/// shown once, at creation.
/// </summary>
public sealed class AccessToken
{
    public int Key { get; set; }

    public required string Name { get; set; }

    /// <summary>Lower-case hex SHA-256 of the full token string.</summary>
    public required string Hash { get; set; }

    /// <summary>The first characters of the token, for recognising it in lists.</summary>
    public required string Prefix { get; set; }

    /// <summary>Null means the token applies to every feed.</summary>
    public int? FeedKey { get; set; }

    public Feed? Feed { get; set; }

    /// <summary>
    /// The account a personal key belongs to. Null for a service token, which an admin manages and which belongs to nobody.
    /// A personal key never does more than its owner may do at the moment it is used.
    /// </summary>
    public int? UserKey { get; set; }

    public User? User { get; set; }

    public TokenScopes Scopes { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? ExpiresUtc { get; set; }

    public DateTime? LastUsedUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }
}

[Flags]
public enum TokenScopes
{
    None = 0,
    Read = 1,
    Push = 2,
    Delete = 4,
    Admin = 8,
}
