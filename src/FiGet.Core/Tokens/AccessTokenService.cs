using System.Security.Cryptography;
using System.Text;
using FiGet.Core.Entities;
using FiGet.Core.Stores;

namespace FiGet.Core.Tokens;

/// <summary>A token that passed validation, with what it may do.</summary>
public sealed record ValidatedToken(int Key, string Name, TokenScopes Scopes, int? FeedKey)
{
    public bool IsAdmin => Scopes.HasFlag(TokenScopes.Admin);

    /// <summary>Admin covers everything; otherwise the scope must be granted and the feed must match.</summary>
    public bool Allows(TokenScopes scope, int feedKey)
    {
        if (IsAdmin)
        {
            return true;
        }

        if (FeedKey is not null && FeedKey != feedKey)
        {
            return false;
        }

        // Push and Delete imply Read: a client that may publish must be able to see what it published.
        var effective = Scopes;
        if ((effective & (TokenScopes.Push | TokenScopes.Delete)) != 0)
        {
            effective |= TokenScopes.Read;
        }

        return effective.HasFlag(scope);
    }
}

/// <summary>The result of creating a token. <see cref="Secret"/> is never stored and never shown again.</summary>
public sealed record CreatedToken(AccessToken Token, string Secret);

public sealed class AccessTokenService(IAccessTokenStore store, TimeProvider time)
{
    private const string SecretPrefix = "figet_";
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    public async Task<CreatedToken> CreateAsync(string name, TokenScopes scopes, int? feedKey, DateTime? expiresUtc, CancellationToken cancellationToken)
    {
        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new AccessToken
        {
            Name = name,
            Hash = HashSecret(secret),
            Prefix = secret[..(SecretPrefix.Length + 6)],
            Scopes = scopes,
            FeedKey = feedKey,
            CreatedUtc = time.GetUtcNow().UtcDateTime,
            ExpiresUtc = expiresUtc,
        };
        await store.AddAsync(token, cancellationToken);
        return new CreatedToken(token, secret);
    }

    /// <summary>Registers a secret chosen by the operator (the bootstrap admin token), unless it already exists.</summary>
    public async Task EnsureAsync(string name, string secret, TokenScopes scopes, CancellationToken cancellationToken)
    {
        var hash = HashSecret(secret);
        if (await store.FindByHashAsync(hash, cancellationToken) is not null)
        {
            return;
        }

        await store.AddAsync(
            new AccessToken
            {
                Name = name,
                Hash = hash,
                Prefix = secret.Length > 12 ? secret[..12] : secret[..Math.Min(4, secret.Length)],
                Scopes = scopes,
                CreatedUtc = time.GetUtcNow().UtcDateTime,
            },
            cancellationToken);
    }

    public async Task<ValidatedToken?> ValidateAsync(string? secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var token = await store.FindByHashAsync(HashSecret(secret.Trim()), cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        if (token is null || token.RevokedUtc is not null || (token.ExpiresUtc is not null && token.ExpiresUtc <= now))
        {
            return null;
        }

        if (token.LastUsedUtc is null || now - token.LastUsedUtc > TouchInterval)
        {
            await store.TouchAsync(token.Key, now, cancellationToken);
        }

        return new ValidatedToken(token.Key, token.Name, token.Scopes, token.FeedKey);
    }

    public Task<bool> RevokeAsync(int tokenKey, CancellationToken cancellationToken) =>
        store.RevokeAsync(tokenKey, time.GetUtcNow().UtcDateTime, cancellationToken);

    public static string HashSecret(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
