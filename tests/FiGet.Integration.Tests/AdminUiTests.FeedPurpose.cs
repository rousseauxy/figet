using System.Net;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

/// <summary>What a feed is used for: the commands its pages show and the gallery it may start with, never what it serves.</summary>
public sealed partial class AdminUiTests
{
    [Fact]
    public async Task A_feed_created_for_chocolatey_with_its_gallery_is_a_proxy_of_the_community_repository()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var name = "choco-" + Guid.NewGuid().ToString("N")[..8];

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));
        var form = FormBlock(page, "create-feed");
        var fields = HiddenFields(form);
        fields[FieldName(form, "feed-name")] = name;
        fields[FieldName(form, "feed-purpose")] = nameof(FeedPurpose.Chocolatey);
        fields[FieldName(form, "feed-gallery")] = "true";

        using var content = new FormUrlEncodedContent(fields);
        var created = await HttpAssert.SuccessBodyAsync(await client.PostAsync("/admin/feeds", content));
        Assert.Contains($"Feed &#x27;{name}&#x27; created.", created, StringComparison.Ordinal);
        Assert.Contains("Chocolatey packages", created, StringComparison.Ordinal);

        var feed = (await FindFeedAsync(name))!;
        Assert.Equal(FeedPurpose.Chocolatey, feed.Purpose);
        Assert.Equal(FeedKind.Proxy, feed.Kind);
        var upstream = Assert.Single(feed.Upstreams);
        Assert.Equal(("Chocolatey community", "https://community.chocolatey.org/api/v2", UpstreamKind.V2), (upstream.Name, upstream.Url, upstream.Kind));
    }

    /// <summary>Any client has no one gallery, so the box adds nothing and the feed stays curated.</summary>
    [Fact]
    public async Task The_gallery_box_adds_nothing_to_a_feed_for_any_client()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var name = "anyclient-" + Guid.NewGuid().ToString("N")[..8];

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));
        var form = FormBlock(page, "create-feed");
        var fields = HiddenFields(form);
        fields[FieldName(form, "feed-name")] = name;
        fields[FieldName(form, "feed-gallery")] = "true";

        using var content = new FormUrlEncodedContent(fields);
        await HttpAssert.SuccessBodyAsync(await client.PostAsync("/admin/feeds", content));

        var feed = (await FindFeedAsync(name))!;
        Assert.Equal((FeedPurpose.Any, FeedKind.Curated), (feed.Purpose, feed.Kind));
        Assert.Empty(feed.Upstreams);
    }

    /// <summary>The feed page follows the purpose chosen on the settings page, as long as the commands were not reworded.</summary>
    [Fact]
    public async Task The_feed_page_shows_the_commands_for_what_the_feed_is_used_for()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var feed = await CreateFeedAsync("purpose-" + Guid.NewGuid().ToString("N")[..8], anonymousRead: true);

        var before = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/feeds/{feed}"));
        Assert.Contains("Register-PSResourceRepository", before, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget add source", before, StringComparison.Ordinal);
        Assert.DoesNotContain("choco source add", before, StringComparison.Ordinal);

        var settings = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/feeds/{feed}"));
        var form = FormBlock(settings, "feed-settings");
        var fields = HiddenFields(form);
        fields[FieldName(form, "anonymous-read")] = "true";
        fields[FieldName(form, "purpose")] = nameof(FeedPurpose.Chocolatey);
        using var content = new FormUrlEncodedContent(fields);
        Assert.Contains("Settings saved.", await HttpAssert.SuccessBodyAsync(await client.PostAsync($"/admin/feeds/{feed}", content)), StringComparison.Ordinal);
        Assert.Equal(FeedPurpose.Chocolatey, (await FindFeedAsync(feed))!.Purpose);

        var after = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/feeds/{feed}"));
        Assert.Contains($"choco source add --name {feed} --source ", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Register-PSResourceRepository", after, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget add source", after, StringComparison.Ordinal);

        // The instructions page offers the purpose's commands as the default, so saving them untouched stores nothing.
        var instructions = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/feeds/{feed}/instructions"));
        Assert.Contains("choco install {id} --version {version} --source {feed}", instructions, StringComparison.Ordinal);
    }
}
