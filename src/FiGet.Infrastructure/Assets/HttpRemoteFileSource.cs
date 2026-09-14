using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FiGet.Application.Ports;

namespace FiGet.Infrastructure.Assets;

public sealed class RemoteFetchSettings
{
    /// <summary>How long one fetch may take, body included.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether private, loopback and carrier-grade NAT addresses may be fetched from. Off by default: see
    /// <see cref="HttpRemoteFileSource"/> for why.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>
    /// An HTTP proxy to fetch through, for an instance whose only way out is one (<c>http://proxy:8080</c>, credentials in
    /// the URL when it needs them). Requires <see cref="AllowedHosts"/>: see <see cref="HttpRemoteFileSource"/>.
    /// </summary>
    public Uri? Proxy { get; set; }

    /// <summary>
    /// Host names a fetch may go to, redirects included: <c>download.example.com</c>, or <c>*.example.com</c> for its
    /// subdomains. Empty: any host, which is only possible without a proxy.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; set; } = [];
}

/// <summary>
/// Fetches a file by URL on behalf of a token holder, which makes it a way to make this server send requests
/// - the shape of a server-side request forgery. The answer is stored and then readable, so without a guard
/// anyone allowed to upload could read what the server can reach and they cannot: the cloud metadata service,
/// an admin port on loopback, an internal site.
///
/// So the address is checked where it cannot be dodged: at connect time, for the address actually being
/// connected to. Checking the host name up front would not do - a name can resolve to something harmless
/// when checked and to something else a moment later, and a redirect can point anywhere. Every connection,
/// including each one a redirect opens, goes through <see cref="ConnectAsync"/>.
///
/// Link-local addresses (where cloud metadata services live) are refused even when private networks are
/// allowed.
///
/// Through a proxy that check cannot work: the connection is to the proxy, and the proxy resolves the name. What still
/// holds there is the host name, checked against an allow-list before every request, redirects included - so a proxy is
/// only used with an allow-list, and a fetch without one is refused. Redirects are followed here, one hop at a time, in
/// both modes, so each hop's host is checked; without a proxy each connection's address is checked as well.
/// </summary>
public sealed class HttpRemoteFileSource : IRemoteFileSource, IDisposable
{
    private const int MaxRedirects = 5;
    private readonly RemoteFetchSettings settings;
    private readonly HttpClient client;

    public HttpRemoteFileSource(RemoteFetchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        if (settings.Proxy is { } proxy)
        {
            var webProxy = new WebProxy(new UriBuilder(proxy) { UserName = "", Password = "" }.Uri) { BypassProxyOnLocal = false };
            if (!string.IsNullOrEmpty(proxy.UserInfo))
            {
                var parts = proxy.UserInfo.Split(':', 2);
                webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }

            handler.UseProxy = true;
            handler.Proxy = webProxy;
        }
        else
        {
            handler.UseProxy = false;
            handler.ConnectCallback = ConnectAsync;
        }

        client = new HttpClient(handler)
        {
            // The whole fetch is bounded below, body included; the client's own timeout would stop at the headers.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FiGet", "1.0"));
    }

    public async Task<RemoteFile> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            throw new RemoteFetchException("Only http and https URLs can be fetched.", refused: true);
        }

        if (settings.Proxy is not null && settings.AllowedHosts.Count == 0)
        {
            throw new RemoteFetchException(
                "Fetching through a proxy needs a list of allowed hosts (FiGet:Assets:RemoteFetch:AllowedHosts): through a proxy this server cannot see which address it reaches.",
                refused: true);
        }

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        HttpResponseMessage? response = null;
        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                if (!IsHostAllowed(current.Host, settings.AllowedHosts))
                {
                    throw new RemoteFetchException($"'{current.Host}' is not in the hosts this server fetches from (FiGet:Assets:RemoteFetch:AllowedHosts).", refused: true);
                }

                response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                {
                    break;
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                response = null;
                if (hop == MaxRedirects || next.Scheme is not ("http" or "https"))
                {
                    throw new RemoteFetchException(hop == MaxRedirects ? "The remote server redirected too many times." : "The remote server redirected to something other than http or https.", refused: true);
                }

                current = next;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RemoteFetchException($"The remote server answered {(int)response.StatusCode} {response.ReasonPhrase}.", refused: false);
            }

            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            var contentType = response.Content.Headers.ContentType?.ToString();
            var content = await response.Content.ReadAsStreamAsync(timeout.Token);

            // From here the caller owns the response and the timeout, through the returned file.
            var owner = new Owner(response, timeout);
            response = null;
            return new RemoteFile(content, contentType, Uri.UnescapeDataString(finalUrl.Segments.LastOrDefault()?.TrimEnd('/') ?? ""), owner);
        }
        catch (HttpRequestException ex) when (ex.InnerException is RemoteFetchException refusal)
        {
            throw refusal;
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteFetchException("The remote server could not be reached: " + ex.Message, refused: false, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteFetchException("The remote server took too long.", refused: false, ex);
        }
        finally
        {
            if (response is not null)
            {
                response.Dispose();
                timeout.Dispose();
            }
        }
    }

    public void Dispose() => client.Dispose();

    /// <summary>Whether a host is in the allow-list: an exact name, or <c>*.example.com</c> for any name below it. An empty list allows every host.</summary>
    public static bool IsHostAllowed(string host, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (allowedHosts.Count == 0)
        {
            return true;
        }

        var name = (host ?? "").TrimEnd('.').ToLowerInvariant();
        foreach (var pattern in allowedHosts.Select(p => p.Trim().TrimEnd('.').ToLowerInvariant()).Where(p => p.Length > 0))
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal)
                ? name.EndsWith(pattern[1..], StringComparison.Ordinal) && name.Length > pattern.Length - 1
                : name == pattern)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an address may be fetched from. Public addresses always; private ones only when allowed; the
    /// link-local block never, because that is where cloud metadata services answer.
    /// </summary>
    public static bool IsAllowed(IPAddress address, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254)
            {
                return false;
            }

            if (b[0] == 0 || b[0] >= 224)
            {
                return false;
            }

            // Reserved for documentation, benchmarks and protocol assignments (RFC 5737, RFC 2544, RFC 6890): nothing
            // legitimate answers there, and a fetch that resolves to one is a name pointing somewhere it should not.
            if ((b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                || (b[0] == 198 && b[1] is 18 or 19)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113))
            {
                return false;
            }

            var isPrivate = b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return !isPrivate || allowPrivateNetworks;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // IPv6 forms that carry an IPv4 address inside them - NAT64 (64:ff9b::/96) and 6to4 (2002::/16) -
            // are judged by that address, or 64:ff9b::a9fe:a9fe would reach the metadata service.
            var v6 = address.GetAddressBytes();
            if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6[4..12].All(x => x == 0))
            {
                return IsAllowed(new IPAddress(v6[12..16]), allowPrivateNetworks);
            }

            if (v6[0] == 0x20 && v6[1] == 0x02)
            {
                return IsAllowed(new IPAddress(v6[2..6]), allowPrivateNetworks);
            }

            var isPrivate = IPAddress.IsLoopback(address) || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
            return !isPrivate || allowPrivateNetworks;
        }

        return false;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        // All or nothing: a name that resolves to one public and one private address is refused, rather than
        // connecting to whichever answers first.
        if (addresses.Length == 0 || !addresses.All(a => IsAllowed(a, settings.AllowPrivateNetworks)))
        {
            throw new RemoteFetchException(
                $"'{host}' resolves to an address this server does not fetch from"
                + (settings.AllowPrivateNetworks ? "." : " (private and local networks are off; see FiGet:Assets:RemoteFetch:AllowPrivateNetworks)."),
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

    private sealed class Owner(HttpResponseMessage response, IDisposable timeout) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose();
            timeout.Dispose();
        }
    }
}
