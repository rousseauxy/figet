using System.Text;
using FiGet.Application.Connectors;
using FiGet.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace FiGet.Http;

/// <summary>
/// Tells a client that pushed an id an upstream also holds. <c>X-NuGet-Warning</c> is the header nuget.exe, the
/// dotnet CLI and PSResourceGet print after a push, so the warning reaches the person publishing without any
/// protocol of FiGet's own; a client that ignores it loses nothing.
/// </summary>
public static class PushWarning
{
    public const string Header = "X-NuGet-Warning";

    public static async Task AddAsync(HttpContext http, ConnectorService connector, Feed feed, string? id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(connector);
        if (string.IsNullOrEmpty(id) || http.Response.HasStarted)
        {
            return;
        }

        if (await connector.PushWarningAsync(feed, id, cancellationToken) is { } warning)
        {
            http.Response.Headers[Header] = HeaderSafe(warning);
        }
    }

    /// <summary>A header value is ASCII on one line; an upstream name typed into the admin page need not be.</summary>
    private static string HeaderSafe(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return builder.ToString();
    }
}
