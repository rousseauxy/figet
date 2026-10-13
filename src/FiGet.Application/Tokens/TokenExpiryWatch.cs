using System.Globalization;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Application.Tokens;

/// <summary>A token that ends soon and has not been warned about at this distance yet.</summary>
public sealed record TokenExpiryWarning(AccessToken Token, int DaysLeft, int Threshold);

/// <summary>
/// When a token that ends is worth a warning. A key that expires does so silently for whoever uses it: the first sign is
/// a pipeline failing on a Monday. Two warnings, far enough ahead to act on and close enough not to be forgotten.
/// </summary>
public static class TokenExpiry
{
    /// <summary>How many days before its end a token is warned about, widest first.</summary>
    public static readonly IReadOnlyList<int> Thresholds = [14, 3];

    /// <summary>
    /// Whole days until the token ends, rounded up so that "ends in 2 days" is never said of one ending in 49 hours' time
    /// less a minute as "1". Null for a token that does not end, is revoked, or has already ended: none has days left.
    /// </summary>
    public static int? DaysLeft(AccessToken token, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.RevokedUtc is not null || token.ExpiresUtc is not { } expires || expires <= nowUtc)
        {
            return null;
        }

        return (int)Math.Ceiling((expires - nowUtc).TotalDays);
    }

    /// <summary>Whether the token ends within the widest threshold: what a list marks.</summary>
    public static bool EndsSoon(AccessToken token, DateTime nowUtc) => DaysLeft(token, nowUtc) <= Thresholds[0];

    /// <summary>
    /// The threshold to warn at now, or null. The narrowest one the token is inside, unless that one - or a narrower one -
    /// was already warned at: a token made with five days to live gets one warning then and one at three days, and a
    /// token inside three days when first seen gets that one only.
    /// </summary>
    public static int? Due(int daysLeft, int? alreadyWarnedAt)
    {
        int? inside = null;
        foreach (var threshold in Thresholds)
        {
            if (daysLeft <= threshold)
            {
                inside = threshold;
            }
        }

        return inside is { } due && (alreadyWarnedAt is null || due < alreadyWarnedAt) ? due : null;
    }
}

/// <summary>
/// Finds the tokens to warn about and remembers that it did, so a warning is given once per threshold however often it
/// looks and whichever replica looks. What was warned at is a setting per token: no column, and gone with nothing to
/// clean when the token is.
/// </summary>
public sealed class TokenExpiryWatch(IAccessTokenStore tokens, ISettingStore settings, TimeProvider time)
{
    public static string SettingKey(int tokenKey) => "token-expiry:warned:" + tokenKey.ToString(CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<TokenExpiryWarning>> TakeDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = new List<TokenExpiryWarning>();
        foreach (var token in await tokens.ListAsync(cancellationToken))
        {
            if (TokenExpiry.DaysLeft(token, now) is not { } daysLeft || daysLeft > TokenExpiry.Thresholds[0])
            {
                continue;
            }

            var key = SettingKey(token.Key);
            int? warnedAt = int.TryParse(await settings.GetAsync(key, cancellationToken), NumberStyles.None, CultureInfo.InvariantCulture, out var stored) ? stored : null;
            if (TokenExpiry.Due(daysLeft, warnedAt) is not { } threshold)
            {
                continue;
            }

            await settings.SetAsync(key, threshold.ToString(CultureInfo.InvariantCulture), "system", cancellationToken);
            due.Add(new TokenExpiryWarning(token, daysLeft, threshold));
        }

        return due;
    }
}
