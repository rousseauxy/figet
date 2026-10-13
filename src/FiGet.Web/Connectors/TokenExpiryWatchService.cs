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
            if (!await JobLeaseGate.TakeAsync(scopes, time, JobLeaseNames.TokenExpiryWatch, Interval, logger, stoppingToken))
            {
                continue;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                foreach (var warning in await scope.ServiceProvider.GetRequiredService<TokenExpiryWatch>().TakeDueAsync(stoppingToken))
                {
                    Report(warning);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A database that is away for a moment must not end the watch for good; the next round looks again.
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Report(TokenExpiryWarning warning)
    {
        var token = warning.Token;
        var name = token.User is { } owner ? $"{owner.UserName}/{token.Name}" : token.Name;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not look for tokens that end soon; trying again at the next round.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
