using System.Net;
using System.Text.RegularExpressions;

namespace FiGet.Integration.Tests.Infrastructure;

/// <summary>Signs a cookie-keeping client in through the real sign-in page, antiforgery token and all.</summary>
public static partial class BrowserSignIn
{
    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string userName = FiGetServerFixture.AdminUserName,
        string password = FiGetServerFixture.AdminPassword)
    {
        ArgumentNullException.ThrowIfNull(client);
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/account/login/local"));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match hidden in HiddenInput().Matches(page))
        {
            fields[WebUtility.HtmlDecode(hidden.Groups["name"].Value)] = WebUtility.HtmlDecode(hidden.Groups["value"].Value);
        }

        fields[InputName(page, "username")] = userName;
        fields[InputName(page, "password")] = password;
        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync("/account/login/local", content);
    }

    /// <summary>The generated name of the input, textarea or select with this id; Blazor derives it from the model path.</summary>
    public static string InputName(string html, string id)
    {
        var match = Regex.Match(html, $"<(?:input|textarea|select)[^>]*id=\"{id}\"[^>]*name=\"(?<name>[^\"]+)\"|<(?:input|textarea|select)[^>]*name=\"(?<name>[^\"]+)\"[^>]*id=\"{id}\"");
        if (!match.Success)
        {
            throw new InvalidOperationException($"No input with id '{id}' in the page.");
        }

        return WebUtility.HtmlDecode(match.Groups["name"].Value);
    }

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();
}
