using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FiGet.Http;

public sealed class PublicUrlOptions
{
    /// <summary>
    /// Absolute base URL used in every URL the protocols emit, for example <c>https://packages.example.org</c>.
    /// When empty, the request's scheme, host and path base are used (honour X-Forwarded-* by setting
    /// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> behind a proxy).
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

public static class PublicUrls
{
    /// <summary>Cookies marked Secure whenever the public address is HTTPS, rather than whenever a request happened to arrive over it.</summary>
    public static CookieSecurePolicy CookiePolicy(PublicUrlOptions options) =>
        options?.PublicBaseUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;

    /// <summary>The base URL without a trailing slash.</summary>
    public static string Base(HttpContext http)
    {
        var configured = http.RequestServices.GetService<IOptions<PublicUrlOptions>>()?.Value.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var request = http.Request;
        return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }

    /// <summary>
    /// The base URL for what a person copies from a feed's pages: the feed's own client address when it has one, else the
    /// public base URL. Never used in protocol answers, which a client must be able to follow from wherever it asked.
    /// </summary>
    public static string ClientBase(HttpContext http, string? clientBaseUrl) =>
        string.IsNullOrWhiteSpace(clientBaseUrl) ? Base(http) : clientBaseUrl.Trim().TrimEnd('/');

    /// <summary><c>{base}/nuget/{feed}</c>, using the feed's canonical name.</summary>
    public static string Feed(HttpContext http, string feedName) =>
        $"{Base(http)}/nuget/{Uri.EscapeDataString(feedName)}";
}
