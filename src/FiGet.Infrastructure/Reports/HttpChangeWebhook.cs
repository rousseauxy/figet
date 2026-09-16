using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using FiGet.Application.Ports;
using FiGet.Infrastructure.Assets;

namespace FiGet.Infrastructure.Reports;

/// <summary>Where a change report is posted, and under what rules.</summary>
public sealed class ChangeWebhookSettings
{
    /// <summary>Where to post. Empty switches the whole thing off. A secret: it usually carries a token in its path.</summary>
    public string? Url { get; set; }

    /// <summary>One extra request header, for a receiver that wants a bearer token rather than a token in the URL.</summary>
    public string? HeaderName { get; set; }

    /// <summary>Its value. Also a secret, and never logged.</summary>
    public string? HeaderValue { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether the URL may resolve into a private or loopback address - an internal relay, typically.</summary>
    public bool AllowPrivateNetworks { get; set; }
}

/// <summary>
/// Posts a report over HTTP.
///
/// Three rules it exists to keep, each of which has been somebody's incident somewhere:
/// the URL never reaches a log, an audit entry, a page or an exception message, because it is the credential;
/// redirects are not followed, because a 3xx would re-send the whole body to an address nobody vetted;
/// and every address the host resolves to is checked, all or nothing, so a name answering with one public and one
/// private address is refused rather than raced.
/// </summary>
public sealed class HttpChangeWebhook : IChangeWebhook, IDisposable
{
    private readonly ChangeWebhookSettings settings;
    private readonly HttpClient? client;
    private readonly Uri? url;

    public HttpChangeWebhook(ChangeWebhookSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        if (string.IsNullOrWhiteSpace(settings.Url)
            || !Uri.TryCreate(settings.Url.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return;
        }

        url = parsed;
        var handler = new SocketsHttpHandler
        {
            // A redirect would re-send the body somewhere else; the receiver is configuration, not a suggestion.
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = ConnectAsync,
        };

        client = new HttpClient(handler) { Timeout = settings.Timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FiGet", "1.0"));
    }

    public bool Configured => client is not null;

    /// <summary>Scheme and host, never the path or the query: those are where a receiver keeps its token.</summary>
    public string Host => url is null ? "" : $"{url.Scheme}://{url.Authority}";

    public async Task<WebhookResult> PostAsync(string json, CancellationToken cancellationToken)
    {
        if (client is null || url is null)
        {
            return new WebhookResult(false, "", "No webhook is configured.");
        }

        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrWhiteSpace(settings.HeaderName) && settings.HeaderValue is not null)
            {
                request.Headers.TryAddWithoutValidation(settings.HeaderName, settings.HeaderValue);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return new WebhookResult(false, Host, "The receiver redirected, and FiGet does not follow redirects for a webhook.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // A receiver's own words help, but only a little of them, and never enough to carry a body back.
                var said = await ReadShortlyAsync(response, cancellationToken);
                return new WebhookResult(false, Host, $"The receiver answered {(int)response.StatusCode}{(said.Length == 0 ? "" : ": " + said)}");
            }

            return new WebhookResult(true, Host, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or TaskCanceledException or OperationCanceledException or RemoteFetchException)
        {
            // The framework's own message can carry the request URI, so this writes its own rather than pass one on.
            return new WebhookResult(false, Host, Describe(ex));
        }
    }

    public void Dispose() => client?.Dispose();

    private static string Describe(Exception ex)
    {
        // The refusal is what an operator needs to read, and it may sit a couple of exceptions down.
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is RemoteFetchException refused)
            {
                return refused.Message;
            }
        }

        return ex switch
        {
            TaskCanceledException or OperationCanceledException => "The receiver did not answer in time.",
            SocketException socket => $"The receiver could not be reached ({socket.SocketErrorCode}).",
            _ when ex.InnerException is SocketException inner => $"The receiver could not be reached ({inner.SocketErrorCode}).",
            _ => "The receiver could not be reached.",
        };
    }

    private static async Task<string> ReadShortlyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var trimmed = body.Trim().ReplaceLineEndings(" ");
            return trimmed.Length <= 200 ? trimmed : trimmed[..200];
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return "";
        }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        // The same all-or-nothing rule as fetching a file from a URL, and the same reason: a name that answers with
        // one public and one private address must not decide by which one connects first.
        if (addresses.Length == 0 || !addresses.All(a => HttpRemoteFileSource.IsAllowed(a, settings.AllowPrivateNetworks)))
        {
            // Thrown as itself, so the reason survives the handler wrapping it: the message below is the only
            // explanation anyone gets, and "could not be reached" would send an operator looking at the network.
            throw new RemoteFetchException(
                $"'{host}' resolves to an address this server does not post to"
                + (settings.AllowPrivateNetworks ? "." : " (private and local networks are off; see FiGet:Changes:Webhook:AllowPrivateNetworks)."),
                refused: true);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
