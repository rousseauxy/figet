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
    /// The feed overview and the package page agree on the version they show (reported by the tester, 2026-09-13): an old cached copy
    /// must not read as the latest while the upstream has newer, and the latest shown is the stable one unless
    /// prerelease is switched on - on both pages, carried from one to the other.
    /// </summary>
    [Fact]
    public async Task The_overview_and_the_package_page_show_the_same_latest_and_prerelease_is_a_switch()
    {
        var id = FiGetServerFixture.UniqueId("Latest.Agrees");
        using (var old = TestPackages.Create(id, "1.0.0"))
        {
            server.Upstream.Add(id, "1.0.0", old.ToArray());
        }

        server.Upstream.AddVersions(id, ["2.0.0", "3.0.0-nightly.1"]);
        using var client = server.CreateClient();
        var lower = id.ToLowerInvariant();
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/proxy/v3/flatcontainer/{lower}/1.0.0/{lower}.1.0.0.nupkg"));

        var overview = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/proxy?q={id}&src=cached"));
        Assert.Contains("<td class=\"fg-num\">2.0.0", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("<td class=\"fg-num\">1.0.0", overview, StringComparison.Ordinal);
        Assert.Contains("title=\"The upstream this package comes from\">stub</span>", overview, StringComparison.Ordinal);

        var package = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/proxy/packages/{id}"));
        Assert.Contains($"-RequiredVersion 2.0.0 ", package, StringComparison.Ordinal);
        Assert.Contains(">from stub</span>", package, StringComparison.Ordinal);
        Assert.Contains("1 prerelease version hidden", package, StringComparison.Ordinal);
        Assert.DoesNotContain(">3.0.0-nightly.1</a>", package, StringComparison.Ordinal);

        var withPrerelease = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/proxy?q={id}&src=cached&pre=1"));
        Assert.Contains("<td class=\"fg-num\">3.0.0-nightly.1", withPrerelease, StringComparison.Ordinal);
        Assert.Contains($"packages/{id}?pre=1", withPrerelease, StringComparison.Ordinal);

        package = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/proxy/packages/{id}?pre=1"));
        Assert.Contains("-RequiredVersion 3.0.0-nightly.1 ", package, StringComparison.Ordinal);
        Assert.Contains(">3.0.0-nightly.1</a>", package, StringComparison.Ordinal);
    }

    /// <summary>
    /// A search on a proxy feed pages this feed's matches and the upstreams' as one list (reported by the tester, 2026-09-14): every page
    /// used to repeat the first 50 upstream hits, so page 2 showed page 1 again and there was no page 3. Each hit appears
    /// once across the pages, the one this feed holds is not listed again as an upstream hit, and the last page has no Next.
    /// </summary>
    [Fact]
    public async Task A_search_pages_through_the_upstream_results_without_repeating_them()
    {
        var prefix = "Paged" + Guid.NewGuid().ToString("N")[..8];
        var ids = Enumerable.Range(1, 120).Select(n => $"{prefix}.M{n:D3}").ToList();
        foreach (var id in ids)
        {
            server.Upstream.AddVersions(id, ["1.0.0"]);
        }

        // One of them held here as well: listed once, as this feed's own.
        using (var held = TestPackages.Create(ids[70], "1.0.0"))
        {
            server.Upstream.Add(ids[70], "1.0.0", held.ToArray());
        }

        using var client = server.CreateClient();
        var lower = ids[70].ToLowerInvariant();
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/proxy/v3/flatcontainer/{lower}/1.0.0/{lower}.1.0.0.nupkg"));

        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/proxy?q={prefix}&page={page}"));
            var onPage = System.Text.RegularExpressions.Regex.Matches(body, $"href=\"/feeds/proxy/packages/({prefix}[^\"?]*)")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            seen.AddRange(onPage);
            Assert.Equal(page < 3, body.Contains($"feeds/proxy?page={page + 1}&amp;q={prefix}", StringComparison.Ordinal));
            Assert.Equal(page < 3 ? 50 : 20, onPage.Count);
        }

        Assert.Equal(120, seen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ids[70], seen[0], StringComparer.OrdinalIgnoreCase);
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
