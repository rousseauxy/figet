using System.Security.Cryptography;
using System.Text;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Application.Tokens;

/// <summary>The account behind a personal key, as it is now: its role is read at validation, not when the key was made.</summary>
public sealed record KeyOwner(int Key, string UserName, UserRole Role)
{
    public AccountActor Actor => new(Key, Role);
}

/// <summary>A token that passed validation, with what it may do.</summary>
public sealed record ValidatedToken(int Key, string Name, TokenScopes Scopes, int? FeedKey, KeyOwner? Owner = null)
{
    public bool IsAdmin => Scopes.HasFlag(TokenScopes.Admin);

    /// <summary>How the request log names it: a personal key with its owner, so two people's "ci" keys stay apart.</summary>
    public string LogName => Owner is null ? Name : $"{Owner.UserName}/{Name}";

    /// <summary>
    /// The token's own limits: admin covers everything; otherwise the scope must be granted and the feed must match. For a
    /// personal key this is only half the answer - see <see cref="AccessTokenService.AllowsAsync"/>.
    /// </summary>
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

public enum TokenCreateStatus
{
    Created,
    NameRequired,
    NoScope,

    /// <summary>The key would carry more than the account creating it holds (docs/auth-plan.md, the ceiling rule).</summary>
    AboveYourRights,
}

public sealed record TokenCreation(TokenCreateStatus Status, CreatedToken? Created = null);

/// <summary>
/// Creates, validates and revokes tokens. The ceiling rule lives here rather than on a page: nobody creates a key with
/// more rights than they hold, whichever page or endpoint asks.
/// </summary>
public sealed class AccessTokenService(IAccessTokenStore store, IUserStore users, FeedAccessService access, TimeProvider time)
{
    private const string SecretPrefix = "figet_";
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A service token: belongs to nobody, managed by admins. Only an admin creates one, and only a super admin one that
    /// carries instance-wide admin rights.
    /// </summary>
    public Task<TokenCreation> CreateServiceTokenAsync(AccountActor creator, string name, TokenScopes scopes, int? feedKey, DateTime? expiresUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creator);
        if (creator.Role < UserRole.Admin || (scopes.HasFlag(TokenScopes.Admin) && creator.Role != UserRole.SuperAdmin))
        {
            return Task.FromResult(new TokenCreation(TokenCreateStatus.AboveYourRights));
        }

        return MintAsync(name, scopes, feedKey, ownerKey: null, expiresUtc, cancellationToken);
    }

    /// <summary>
    /// A personal key for the account itself. Its scopes and feed are limits, not grants: at use it does the lower of what
    /// they allow and what the owner may do then. A key limited to one feed is refused when the owner cannot already do
    /// that there, so a key page never offers what the account does not have.
    /// </summary>
    public async Task<TokenCreation> CreatePersonalKeyAsync(AccountActor owner, string name, TokenScopes scopes, Feed? feed, DateTime? expiresUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (scopes.HasFlag(TokenScopes.Admin))
        {
            return new TokenCreation(TokenCreateStatus.AboveYourRights);
        }

        if (feed is not null && RequiredLevel(scopes) is var required && required > FeedAccessLevel.None
            && await access.LevelAsync(feed, owner, cancellationToken) < required)
        {
            return new TokenCreation(TokenCreateStatus.AboveYourRights);
        }

        return await MintAsync(name, scopes, feed?.Key, owner.Key, expiresUtc, cancellationToken);
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

    /// <summary>
    /// The token for a secret, or null. A personal key whose owner is gone or disabled is no key at all, and reads the same
    /// as a revoked one.
    /// </summary>
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

        KeyOwner? owner = null;
        if (token.UserKey is { } userKey)
        {
            var user = await users.FindAsync(userKey, cancellationToken);
            if (user is null || user.IsDisabled)
            {
                return null;
            }

            owner = new KeyOwner(user.Key, user.UserName, user.Role);
        }

        if (token.LastUsedUtc is null || now - token.LastUsedUtc > TouchInterval)
        {
            await store.TouchAsync(token.Key, now, cancellationToken);
        }

        return new ValidatedToken(token.Key, token.Name, token.Scopes, token.FeedKey, owner);
    }

    /// <summary>
    /// Why a secret that did not validate was refused, for the audit log: the token's name and what is wrong with it, or
    /// null when it is no token at all. Never anything of the secret itself - a Basic password may be a person's password.
    /// </summary>
    public async Task<(string Name, string Reason)?> DescribeRefusedAsync(string secret, CancellationToken cancellationToken)
    {
        var token = await store.FindByHashAsync(HashSecret(secret.Trim()), cancellationToken);
        if (token is null)
        {
            return null;
        }

        var reason = token.RevokedUtc is not null ? "revoked"
            : token.ExpiresUtc is not null && token.ExpiresUtc <= time.GetUtcNow().UtcDateTime ? "expired"
            : "owner disabled or deleted";
        return (token.Name, reason);
    }

    /// <summary>
    /// Whether a validated token may do <paramref name="scope"/> on <paramref name="feed"/>: its own limits, and for a
    /// personal key also its owner's level on the feed right now, so a grant removed or a group left applies at once.
    /// </summary>
    public async Task<bool> AllowsAsync(ValidatedToken token, TokenScopes scope, Feed feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(feed);
        if (!token.Allows(scope, feed.Key))
        {
            return false;
        }

        return token.Owner is null
            || await access.LevelAsync(feed, token.Owner.Actor, cancellationToken) >= RequiredLevel(scope);
    }

    /// <summary>Revokes any token. For the admin tokens page, which admins alone reach.</summary>
    public Task<bool> RevokeAsync(int tokenKey, CancellationToken cancellationToken) =>
        store.RevokeAsync(tokenKey, time.GetUtcNow().UtcDateTime, cancellationToken);

    /// <summary>Revokes one of the account's own keys; false for a key that is not theirs.</summary>
    public Task<bool> RevokeOwnAsync(AccountActor owner, int tokenKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return store.RevokeOwnedAsync(owner.Key, tokenKey, time.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>The feed level the scopes need: pushing, deleting and relisting are publishing; reading is reading.</summary>
    public static FeedAccessLevel RequiredLevel(TokenScopes scopes) =>
        (scopes & (TokenScopes.Push | TokenScopes.Delete | TokenScopes.Admin)) != 0 ? FeedAccessLevel.Publish
        : scopes.HasFlag(TokenScopes.Read) ? FeedAccessLevel.Read
        : FeedAccessLevel.None;

    public static string HashSecret(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private async Task<TokenCreation> MintAsync(string name, TokenScopes scopes, int? feedKey, int? ownerKey, DateTime? expiresUtc, CancellationToken cancellationToken)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0)
        {
            return new TokenCreation(TokenCreateStatus.NameRequired);
        }

        if ((scopes & (TokenScopes.Read | TokenScopes.Push | TokenScopes.Delete | TokenScopes.Admin)) == TokenScopes.None)
        {
            return new TokenCreation(TokenCreateStatus.NoScope);
        }

        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new AccessToken
        {
            Name = name,
            Hash = HashSecret(secret),
            Prefix = secret[..(SecretPrefix.Length + 6)],
            Scopes = scopes,
            FeedKey = feedKey,
            UserKey = ownerKey,
            CreatedUtc = time.GetUtcNow().UtcDateTime,
            ExpiresUtc = expiresUtc,
        };
        await store.AddAsync(token, cancellationToken);
        return new TokenCreation(TokenCreateStatus.Created, new CreatedToken(token, secret));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
