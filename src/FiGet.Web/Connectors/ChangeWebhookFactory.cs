using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Application.Reports;
using FiGet.Infrastructure.Reports;
using FiGet.Web.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <summary>
/// Where a feed's change report goes, and the sender that posts it.
///
/// An address may come from three places, in this order: the feed's own, an administrator's for the whole server, or
/// configuration. The first two live in the settings table **encrypted** with the data-protection key ring - the same
/// ring that protects sign-in cookies - because a webhook URL usually carries its token in its path, and a setting
/// somebody can read back on a page is not a place for one. Configuration stays the fallback, so a deployment that
/// prefers to hold its secrets in the environment can.
/// </summary>
public sealed class ChangeWebhookFactory(
    ISettingStore settings,
    IDataProtectionProvider protection,
    IOptions<FiGetOptions> options,
    ILogger<ChangeWebhookFactory> logger)
{
    /// <summary>Named once: a purpose is part of the key, so a value encrypted here cannot be read as another.</summary>
    private const string Purpose = "figet.changes.webhook";

    private readonly IDataProtector protector = protection.CreateProtector(Purpose);

    /// <summary>The address of the whole server's webhook, as an administrator set it.</summary>
    public const string ServerKey = "changes:webhook-url";

    /// <summary>The address of one feed's own webhook. One webhook per channel is this, and nothing else.</summary>
    public static string FeedKey(int feedKey) => $"changes:webhook-url:{feedKey}";

    /// <summary>Up to when this feed has been reported on, so a restart neither skips a day nor repeats one.</summary>
    public static string ReportedThroughKey(int feedKey) => $"changes:reported-through:{feedKey}";

    /// <summary>A sender for this feed, or null when nothing is configured for it.</summary>
    public async Task<IChangeWebhook?> ForAsync(Feed feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var url = await UrlAsync(FeedKey(feed.Key), cancellationToken)
            ?? await UrlAsync(ServerKey, cancellationToken)
            ?? options.Value.Changes.Webhook.Url;

        return Create(url);
    }

    /// <summary>A sender for the server's own address, for the administrator page's test.</summary>
    public async Task<IChangeWebhook?> ServerAsync(CancellationToken cancellationToken) =>
        Create(await UrlAsync(ServerKey, cancellationToken) ?? options.Value.Changes.Webhook.Url);

    /// <summary>Stores an address, encrypted; an empty one removes it and falls back to whatever is below.</summary>
    public async Task SetUrlAsync(string key, string? url, string? actor, CancellationToken cancellationToken) =>
        await settings.SetAsync(key, string.IsNullOrWhiteSpace(url) ? "" : protector.Protect(url.Trim()), actor, cancellationToken);

    public ChangeWebhookFormat Format()
    {
        var configured = options.Value.Changes.Webhook.Format;
        return Enum.TryParse<ChangeWebhookFormat>(configured, ignoreCase: true, out var format) ? format : ChangeWebhookFormat.Json;
    }

    private async Task<string?> UrlAsync(string key, CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(key, cancellationToken);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // A key ring rebuilt without its master key cannot read what the old one wrote. Say so once, plainly,
            // rather than post a report to nowhere or throw inside a background job.
            logger.LogWarning(ex, "The stored webhook address under {Key} cannot be read with the current key ring; set it again on the changes page.", key);
            return null;
        }
    }

    private HttpChangeWebhook? Create(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var webhook = options.Value.Changes.Webhook;
        var sender = new HttpChangeWebhook(new ChangeWebhookSettings
        {
            Url = url,
            HeaderName = webhook.HeaderName,
            HeaderValue = webhook.HeaderValue,
            Timeout = webhook.Timeout,
            AllowPrivateNetworks = webhook.AllowPrivateNetworks,
        });

        if (sender.Configured)
        {
            return sender;
        }

        // A URL that is not an address is a typo somebody must see, not a silent nothing.
        logger.LogWarning("The configured change webhook is not an http or https address, so no report is posted.");
        sender.Dispose();
        return null;
    }
}
