using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using FiGet.Web.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// The storage check (review item S9.2): stray package files - a version nobody has, a feed that is gone - are listed and
/// removed on request; a file a row names, and a file written in the last hour, are neither listed nor removable, even when
/// a hand-made request names them.
/// </summary>
public sealed partial class StorageCheckTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Fact]
    public async Task Stray_files_are_listed_and_removed_and_named_or_new_files_are_left_alone()
    {
        var id = FiGetServerFixture.UniqueId("Storage.Kept");
        using (var package = TestPackages.Create(id, "1.0.0"))
        using (var pusher = server.CreateClient())
        using (var content = new MultipartFormDataContent())
        {
            pusher.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
            content.Add(new StreamContent(package) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "package", "package.nupkg");
            HttpAssert.Status(HttpStatusCode.Created, await pusher.PutAsync("nuget/public/v3/publish", content));
        }

        int publicKey;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            publicKey = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync("public", CancellationToken.None))!.Key;
        }

        var files = Path.Combine(server.Services.GetRequiredService<StoragePaths>().Root, "files");
        var idLower = id.ToLowerInvariant();
        var ghostId = "ghost." + Guid.NewGuid().ToString("N")[..8];
        var kept = $"feeds/{publicKey}/packages/{idLower}/1.0.0/{idLower}.1.0.0.nupkg";
        var ghost = $"feeds/{publicKey}/packages/{ghostId}/9.9.9/{ghostId}.9.9.9.nupkg";
        var fresh = $"feeds/{publicKey}/packages/{ghostId}/9.9.8/{ghostId}.9.9.8.nupkg";
        var goneFeed = $"feeds/{900000 + Random.Shared.Next(99999)}/assets/ab/{Guid.NewGuid():N}";
        foreach (var (path, hoursOld) in new[] { (ghost, 2), (fresh, 0), (goneFeed, 3), (kept, 2) })
        {
            var full = Path.Combine(files, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full))
            {
                await File.WriteAllBytesAsync(full, [1, 2, 3], TestContext.Current.CancellationToken);
            }

            File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddHours(-hoursOld));
        }

        using var admin = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));

        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/storage"));
        var found = await PostFormAsync(admin, page, "storage-scan", []);
        Assert.Contains(ghost, found, StringComparison.Ordinal);
        Assert.Contains(goneFeed, found, StringComparison.Ordinal);
        Assert.DoesNotContain(fresh, found, StringComparison.Ordinal);
        Assert.DoesNotContain(kept, found, StringComparison.Ordinal);

        // A hand-made removal naming the kept and the fresh file as well removes only the stray ones.
        var removedPage = await PostFormAsync(admin, found, "storage-remove", [("Remove.Paths", kept), ("Remove.Paths", fresh)]);
        Assert.Contains("Removed 2 stray files", removedPage, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(files, ghost)));
        Assert.False(File.Exists(Path.Combine(files, goneFeed)));
        Assert.True(File.Exists(Path.Combine(files, kept)));
        Assert.True(File.Exists(Path.Combine(files, fresh)));
        HttpAssert.Status(HttpStatusCode.OK, await server.CreateClient().GetAsync($"nuget/public/package/{id}/1.0.0"));

        // Not for someone who is not an admin.
        HttpAssert.Status(HttpStatusCode.Redirect, await server.CreateClient().GetAsync("/admin/storage"));
    }

    /// <summary>Posts the form whose handler is <paramref name="handler"/>, with its hidden fields and any extra ones.</summary>
    private static async Task<string> PostFormAsync(HttpClient client, string page, string handler, (string Name, string Value)[] extra)
    {
        var form = FormElement().Matches(page).Select(m => m.Value).First(f => f.Contains($"value=\"{handler}\"", StringComparison.Ordinal));
        var fields = HiddenInput().Matches(form)
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups["name"].Value), WebUtility.HtmlDecode(m.Groups["value"].Value)))
            .Concat(extra.Select(e => new KeyValuePair<string, string>(e.Name, e.Value)))
            .ToList();
        using var content = new FormUrlEncodedContent(fields);
        return await HttpAssert.SuccessBodyAsync(await client.PostAsync("/admin/storage", content));
    }

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();
}
