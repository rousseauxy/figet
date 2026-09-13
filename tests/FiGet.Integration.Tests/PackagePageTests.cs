using System.Net;
using System.Net.Http.Headers;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using FiGet.Web.Components.Shared;

namespace FiGet.Integration.Tests;

/// <summary>
/// The version list on a package page. A proxy feed is used because it is the cheap way to get a package
/// with more versions than fit on one page: the upstream stub can register versions without anybody
/// building a nupkg for each one.
/// </summary>
public sealed class PackagePageTests(ProxyServerFixture server) : IClassFixture<ProxyServerFixture>
{
    /// <summary>
    /// Fifty rows by default, and a way to ask for more - which is what a reader looking at a package with
    /// dozens of versions actually wants, rather than paging four times.
    /// </summary>
    [Fact]
    public async Task The_version_list_pages_at_fifty_and_offers_larger_pages()
    {
        var id = FiGetServerFixture.UniqueId("Page.Versions");
        server.Upstream.AddVersions(id, Enumerable.Range(1, 60).Select(n => $"1.0.{n}"));

        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"feeds/proxy/packages/{id}?tab=versions"));

        Assert.Contains("1 to 50 of 60 versions", page, StringComparison.Ordinal);
        Assert.Contains("per page", page, StringComparison.Ordinal);

        var larger = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"feeds/proxy/packages/{id}?tab=versions&per=100"));

        Assert.Contains("1 to 60 of 60 versions", larger, StringComparison.Ordinal);
    }

    /// <summary>
    /// A size nobody offered falls back to the default. The rows are rendered server-side, so without this
    /// a hand-typed <c>per=100000</c> would render every version of a package that has 2098 of them.
    /// </summary>
    [Fact]
    public async Task A_page_size_that_is_not_offered_falls_back_to_the_default()
    {
        var id = FiGetServerFixture.UniqueId("Page.Capped");
        server.Upstream.AddVersions(id, Enumerable.Range(1, 60).Select(n => $"1.0.{n}"));

        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"feeds/proxy/packages/{id}?tab=versions&per=9999"));

        Assert.Contains("1 to 50 of 60 versions", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A package id breaks after its dots rather than mid-word. Ids are long and unspaced, so in a narrow
    /// column they used to wrap at any character - "Microsoft.Entra.A / pplications" - which reads badly on
    /// a phone. Searched for by id rather than browsed, so the row cannot land on a later page when other
    /// tests share the feed.
    /// </summary>
    [Fact]
    public async Task A_long_package_id_may_break_after_its_dots()
    {
        var id = FiGetServerFixture.UniqueId("Page.Wrap.Dotted");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"feeds/public?q={Uri.EscapeDataString(id)}"));

        Assert.Contains("Page.<wbr>Wrap.<wbr>Dotted.<wbr>", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each install command has a copy button carrying exactly the command shown, on the package page and on a
    /// version's page. Reported from testing: with a dark theme a selection was nearly invisible, so selecting a
    /// command by hand left nobody sure what they had copied.
    /// </summary>
    [Fact]
    public async Task Each_install_command_can_be_copied()
    {
        var id = FiGetServerFixture.UniqueId("Page.Copy");
        using (var package = TestPackages.Create(id, "1.2.3"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        foreach (var path in new[] { $"feeds/public/packages/{id}", $"feeds/public/packages/{id}/1.2.3" })
        {
            var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync(path));
            Assert.Contains($"data-copy=\"Install-Module -Name {id} -RequiredVersion 1.2.3 -Repository public\"", page, StringComparison.Ordinal);
            Assert.Contains($"data-copy=\"Install-PSResource -Name {id} -Version 1.2.3 -Repository public\"", page, StringComparison.Ordinal);
            Assert.Contains($"data-copy=\"dotnet add package {id} --version 1.2.3 --source ", page, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The id is encoded before its breaks are inserted. The helper returns markup, which bypasses Razor's
    /// own encoding, and an id comes from whoever pushed the package. A valid id cannot hold angle brackets,
    /// but a helper that emits markup must be safe without leaning on validation somewhere else.
    ///
    /// Asserted as a property rather than an exact string, so it does not care whether the encoder writes
    /// an entity as a name or a number: once the inserted breaks are taken out, no angle bracket may remain.
    /// </summary>
    [Fact]
    public void An_id_is_encoded_before_its_breaks_are_inserted()
    {
        var rendered = Display.BreakableId("a.<script>alert(1)</script>.b").Value;

        Assert.Contains("a.<wbr>", rendered, StringComparison.Ordinal);
        var withoutBreaks = rendered.Replace("<wbr>", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("<", withoutBreaks, StringComparison.Ordinal);
        Assert.DoesNotContain(">", withoutBreaks, StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> PushAsync(string feed, Stream package)
    {
        using var client = server.CreateClient();
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"nuget/{feed}/") { Content = content };
        request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }
}
