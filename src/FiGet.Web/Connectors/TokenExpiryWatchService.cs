using System.Globalization;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Http;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Says, once a day, which tokens end soon: a line in the audit log and a warning in the server's log for each, twice in
/// a token's last two weeks (<see cref="TokenExpiry.Thresholds"/>). One replica looks, the one holding the lease; what
/// was already said is kept in the database, so a restart or another replica does not say it again.
/// </summary>
public sealed partial class TokenExpiryWatchService(
    IServiceScopeFactory scopes,
    AuditLog audit,
    IOptions<FiGetOptions> options,
    TimeProvider time,
    ILogger<TokenExpiryWatchService> logger) : BackgroundService
{
    private TimeSpan Interval => options.Value.Jobs.TokenExpiryWatch;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            // A database that is away for a moment must not end the watch for good: a failed round is recorded and logged,
            // and the next one looks again.
            await JobLeaseGate.RunAsync(scopes, time, JobLeaseNames.TokenExpiryWatch, Interval, logger, LookAsync, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<string> LookAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var warnings = await scope.ServiceProvider.GetRequiredService<TokenExpiryWatch>().TakeDueAsync(cancellationToken);
        foreach (var warning in warnings)
        {
            Report(warning);
        }

        return warnings.Count == 0
            ? "No token needed a new warning."
            : $"Warned about {warnings.Count} token(s): {string.Join(", ", warnings.Select(w => $"{Name(w.Token)} ({w.DaysLeft} d)"))}.";
    }

    private static string Name(AccessToken token) => token.User is { } owner ? $"{owner.UserName}/{token.Name}" : token.Name;

    private void Report(TokenExpiryWarning warning)
    {
        var token = warning.Token;
        var name = Name(token);
        var expires = token.ExpiresUtc!.Value.ToString("u", CultureInfo.InvariantCulture);
        audit.Record(
            null,
            "token.expiring",
            name,
            $"days={warning.DaysLeft} expires={expires} feed={token.Feed?.Name ?? "all"} kind={(token.UserKey is null ? "service" : "personal")}");
        LogExpiring(logger, name, warning.DaysLeft, expires);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The token {Token} ends in {Days} day(s), on {Expires}. Whatever uses it is refused from then on; make its replacement before that.")]
    private static partial void LogExpiring(ILogger logger, string token, int days, string expires);
}
