using System.Globalization;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Web.Configuration;
using Microsoft.Extensions.Options;

namespace FiGet.Web.Connectors;

/// <param name="Host">Scheme and authority only. Never a path: a webhook address carries its token there.</param>
/// <param name="Port">The port to open, as a person would write it in a request to whoever opens them.</param>
/// <param name="Why">What stops working when it is closed.</param>
/// <param name="Source">Which setting or feed asked for it, so a reader can remove the cause rather than the rule.</param>
public sealed record EgressHost(string Host, string Port, string Why, string Source);

/// <summary>
/// What this instance actually has to reach, read from its own configuration.
///
/// The guide can only say "if you proxy the PowerShell Gallery, allow these two"; this says "this server needs these",
/// which is the list somebody pastes into a firewall request. Hosts only - the path of a webhook address is its
/// password, and nothing here is worth leaking one for.
/// </summary>
public sealed class EgressReportService(
    IFeedStore feeds,
    IOidcProviderStore providers,
    IDatabaseFacts database,
    ChangeWebhookFactory webhooks,
    IOptions<FiGetOptions> options)
{
    /// <summary>
    /// Galleries that serve their packages from somewhere other than the host their API is on. Allowing only the API
    /// host gives listings that work and downloads that fail, which is the mistake this whole section exists to stop.
    ///
    /// Each measured by following a download's redirects on 2026-10-06. A gallery not named here is reported by its API
    /// host alone, which is why the page says so: every one of these redirects to a sibling under the same domain, so a
    /// gallery nobody has measured is worth asking about rather than assuming.
    /// </summary>
    private static readonly Dictionary<string, string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["www.powershellgallery.com"] = "cdn.powershellgallery.com",
        ["community.chocolatey.org"] = "packages.chocolatey.org",
        ["www.poshtestgallery.com"] = "psg-int-centralus.poshtestgallery.com",
    };

    public async Task<IReadOnlyList<EgressHost>> ReadAsync(CancellationToken cancellationToken)
    {
        var found = new List<EgressHost>();

        foreach (var feed in await feeds.ListAsync(cancellationToken))
        {
            foreach (var upstream in feed.Upstreams.Where(u => u.Enabled))
            {
                if (!Uri.TryCreate(upstream.Url, UriKind.Absolute, out var url))
                {
                    continue;
                }

                found.Add(new(Authority(url), Port(url), "Listings, versions and metadata.", $"feed {feed.Name} · {upstream.Name}"));
                if (DownloadHosts.TryGetValue(url.Host, out var downloads))
                {
                    found.Add(new(
                        url.Scheme + "://" + downloads,
                        Port(url),
                        "The package bytes: this gallery redirects every download here.",
                        $"feed {feed.Name} · {upstream.Name}"));
                }
            }
        }

        var facts = await database.ReadAsync(cancellationToken);
        if (facts.Engine == DatabaseEngine.SqlServer)
        {
            // The facts carry "host / catalog" and never the connection string, which is the point: this page cannot
            // print a password because it is never given one.
            var host = facts.Location.Split('/', 2)[0].Trim();
            if (host.Length > 0 && host != "unknown")
            {
                found.Add(new(host, host.Contains(',', StringComparison.Ordinal) ? "as named" : "1433", "The database.", "FiGet:Database:ConnectionString"));
            }
        }

        foreach (var provider in (await providers.ListAsync(cancellationToken)).Where(p => p.Enabled))
        {
            if (Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority))
            {
                found.Add(new(Authority(authority), Port(authority), "Discovery, keys and the token exchange at sign-in.", $"sign-in provider {provider.DisplayName}"));
            }
        }

        foreach (var feed in await feeds.ListAsync(cancellationToken))
        {
            if (feed.Kind == FeedKind.Assets)
            {
                continue;
            }

            var webhook = await webhooks.ForAsync(feed, cancellationToken);
            if (webhook is not null)
            {
                found.Add(new(webhook.Host, PortOf(webhook.Host), "Posting the change report.", $"feed {feed.Name} · change report"));
            }

            (webhook as IDisposable)?.Dispose();
        }

        foreach (var host in options.Value.Assets.RemoteFetch.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            found.Add(new(host.Trim(), "443", "Fetching an asset by URL.", "FiGet:Assets:RemoteFetch:AllowedHosts"));
        }

        foreach (var (value, source) in new[]
                 {
                     (options.Value.Assets.RemoteFetch.Proxy, "FiGet:Assets:RemoteFetch:Proxy"),
                     (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"), "OTEL_EXPORTER_OTLP_ENDPOINT"),
                     (Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy"), "HTTPS_PROXY"),
                 })
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var parsed))
            {
                var why = source.StartsWith("OTEL", StringComparison.Ordinal) ? "Traces and metrics." : "Everything above goes through it instead.";
                found.Add(new(Authority(parsed), Port(parsed), why, source));
            }
        }

        // One row per host and port, with every reason that asked for it: a firewall rule is about the destination, and
        // a reader deciding whether they still need it wants all the causes in one place.
        return
        [
            .. found
                .GroupBy(h => (h.Host, h.Port))
                .Select(g => new EgressHost(
                    g.Key.Host,
                    g.Key.Port,
                    string.Join(" ", g.Select(h => h.Why).Distinct(StringComparer.Ordinal)),
                    string.Join(", ", g.Select(h => h.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase))))
                .OrderBy(h => h.Host, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string Authority(Uri url) => url.GetLeftPart(UriPartial.Authority);

    private static string Port(Uri url) => url.Port.ToString(CultureInfo.InvariantCulture);

    private static string PortOf(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var url) ? Port(url) : "443";
}
