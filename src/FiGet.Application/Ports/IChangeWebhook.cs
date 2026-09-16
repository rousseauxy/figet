namespace FiGet.Application.Ports;

/// <summary>What happened to one delivery. Never the URL: that is a credential, see <see cref="IChangeWebhook"/>.</summary>
/// <param name="Host">Scheme and host only, for a log line or an audit entry to name where it went.</param>
/// <param name="Problem">Why it did not arrive, in words this server wrote itself; null when it did.</param>
public sealed record WebhookResult(bool Delivered, string Host, string? Problem);

/// <summary>
/// Posts a report somewhere a deployment chose. Behind a port for three reasons: the URL usually carries its own token
/// in the path (Teams, Slack, Power Automate), so it is a secret and must not reach a log or a page; posting it makes
/// this server send a request somebody else's network sees; and the address rules that guard that live beside the
/// asset fetcher's in Infrastructure.
/// </summary>
public interface IChangeWebhook
{
    /// <summary>False when no URL is configured, which is how the feature is switched off.</summary>
    bool Configured { get; }

    /// <summary>Scheme and host of where reports go, for a page to say so without showing the token in the path.</summary>
    string Host { get; }

    /// <summary>Posts one document. Never throws for a delivery failure: a report is not worth a stack trace.</summary>
    Task<WebhookResult> PostAsync(string json, CancellationToken cancellationToken);
}
