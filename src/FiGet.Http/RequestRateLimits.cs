using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FiGet.Http;

public sealed class RateLimitOptions
{
    /// <summary>
    /// Requests a minute one address may make without a valid key or a sign-in: protocol reads on feeds that allow anonymous
    /// read, asset downloads, and pages. 0 turns the limit off.
    /// </summary>
    public int AnonymousRequestsPerMinute { get; set; } = 1200;

    /// <summary>How many of those may arrive at once, before the per-minute rate applies. An install of a meta-module is a burst.</summary>
    public int AnonymousBurst { get; set; } = 600;

    /// <summary>Sign-in attempts a minute from one address, local and through a provider, signed in or not. 0 turns it off.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 20;
}

/// <summary>
/// Per-address limits for what anyone can do without proving who they are. Token buckets: a burst is allowed, then the
/// rate. Keyed by the connection's address, which the forwarded-headers middleware has already set to the client's when a
/// trusted proxy is in front; never by an <c>X-Forwarded-For</c> header read directly, which any client can write.
///
/// Requests with a valid key or a sign-in are not limited here. That cannot be decided before the request is handled - a
/// garbage key looks like a key until it is checked - so protocol requests are counted in <see cref="FeedAccess"/>, after
/// the key was checked, and only pages and sign-ins are counted by the middleware.
/// </summary>
public sealed class RequestRateLimits : IDisposable
{
    public const string Anonymous = "anonymous";
    public const string SignIn = "signin";

    private readonly PartitionedRateLimiter<string>? anonymous;
    private readonly PartitionedRateLimiter<string>? signIn;

    public RequestRateLimits(IOptions<RateLimitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limits = options.Value;
        anonymous = Create(limits.AnonymousRequestsPerMinute, Math.Max(1, limits.AnonymousBurst));
        signIn = Create(limits.SignInAttemptsPerMinute, Math.Max(1, limits.SignInAttemptsPerMinute));
    }

    /// <summary>
    /// Takes one request from the address's bucket. When it is empty, sets 429 with <c>Retry-After</c> on the response and
    /// returns false; the caller then sends nothing more.
    /// </summary>
    public bool TryAcquire(HttpContext http, string bucket)
    {
        ArgumentNullException.ThrowIfNull(http);
        var limiter = bucket == SignIn ? signIn : anonymous;
        if (limiter is null)
        {
            return true;
        }

        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var lease = limiter.AttemptAcquire(address);
        if (lease.IsAcquired)
        {
            return true;
        }

        var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 1;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        return false;
    }

    public void Dispose()
    {
        anonymous?.Dispose();
        signIn?.Dispose();
    }

    private static PartitionedRateLimiter<string>? Create(int perMinute, int burst)
    {
        if (perMinute <= 0)
        {
            return null;
        }

        // Refilled in small steps, so a client that paces itself never meets the limit, rather than a whole minute's worth
        // arriving at once at the top of each minute: every second for rates of at least one a second, otherwise one token
        // every 60/rate seconds. A whole token every second for a small rate would quietly make it sixty a minute.
        var (tokens, period) = perMinute >= 60
            ? (perMinute / 60, TimeSpan.FromSeconds(1))
            : (1, TimeSpan.FromSeconds(60.0 / perMinute));
        return PartitionedRateLimiter.Create<string, string>(address => RateLimitPartition.GetTokenBucketLimiter(
            address,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = tokens,
                ReplenishmentPeriod = period,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }
}
