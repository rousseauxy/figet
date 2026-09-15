using System.Net;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// The usual feeds, plus two paths that throw - one a page, one under a protocol prefix - so an unhandled
/// failure can be provoked without breaking anything real.
/// </summary>
public sealed class ErrorPageServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public const string ThrowingPage = "/test-only/throw";
    public const string ThrowingProtocolPath = "/api/test-only-throw";

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ThrowingPathFilter>());
    }

    /// <summary>Added after the app's own pipeline, so it is reached only by a request no endpoint answered.</summary>
    private sealed class ThrowingPathFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, following) =>
            {
                if (context.Request.Path == ThrowingPage || context.Request.Path == ThrowingProtocolPath)
                {
                    throw new InvalidOperationException("Provoked by the error page tests.");
                }

                await following(context);
            });
        };
    }
}

/// <summary>
/// Errors a person meets in a browser get a page with the code on it; errors a package client
/// meets keep FiGet's own answer, because what a client reads is part of the protocol.
/// </summary>
public sealed class ErrorPageTests(ErrorPageServerFixture server) : IClassFixture<ErrorPageServerFixture>
{
    [Fact]
    public async Task An_unknown_page_gets_an_error_page_with_the_code()
    {
        using var client = server.CreateClient();
        using var response = await client.GetAsync("/no-such-page-here");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("fg-error-code", page, StringComparison.Ordinal);
        Assert.Contains(">404<", page, StringComparison.Ordinal);
        Assert.Contains("Page not found", page, StringComparison.Ordinal);
    }

    /// <summary>Asked for by the tester: the site is a teapot, as RFC 2324 allows.</summary>
    [Fact]
    public async Task Asking_for_coffee_is_refused_by_a_teapot()
    {
        using var client = server.CreateClient();
        using var page = await client.GetAsync("/coffee");
        HttpAssert.Status((HttpStatusCode)418, page);
        Assert.Contains(">418<", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var brew = await client.SendAsync(new HttpRequestMessage(new HttpMethod("BREW"), "/coffee"));
        HttpAssert.Status((HttpStatusCode)418, brew);
        Assert.Contains("teapot", await brew.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, "Bad request")]
    [InlineData(403, "Access denied")]
    [InlineData(413, "Too large")]
    [InlineData(418, "I&#x27;m a teapot")]
    [InlineData(503, "Service unavailable")]
    public async Task Each_code_has_its_own_explanation_and_answers_with_that_status(int code, string title)
    {
        using var client = server.CreateClient();
        using var response = await client.GetAsync($"/error/{code}");

        Assert.Equal(code, (int)response.StatusCode);
        Assert.Contains(title, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A page that finds nothing - an unknown feed, package or asset directory - gets the error page too. Such a
    /// page sets 404 while rendering, and .NET 10 then drops the page's own markup: before the error page existed,
    /// the live instance answered these with a 404 and an empty body, which is the blank page people saw.
    /// </summary>
    [Theory]
    [InlineData("/feeds/no-such-feed")]
    [InlineData("/feeds/public/packages/No.Such.Package")]
    [InlineData("/assets/no-such-directory")]
    public async Task A_page_that_finds_nothing_shows_the_error_page(string path)
    {
        using var client = server.CreateClient();
        using var response = await client.GetAsync(path);

        HttpAssert.Status(HttpStatusCode.NotFound, response);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("fg-error-code", page, StringComparison.Ordinal);
        Assert.Contains("Page not found", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client reads these answers as they are. Several are deliberately empty - the api/v2 probe's 404 is what
    /// keeps PowerShellGet from rewriting a repository URL - so an HTML page in their place would change the protocol.
    /// </summary>
    [Theory]
    [InlineData("/nuget/no-such-feed/v3/index.json", "application/json")]
    [InlineData("/nuget/public/api/v2/", null)]
    [InlineData("/endpoints/no-such-directory/dir/", "application/json")]
    [InlineData("/api/packages/no-such-feed/versions", "application/json")]
    public async Task Protocol_paths_keep_their_own_error_answers(string path, string? mediaType)
    {
        using var client = server.CreateClient();
        using var response = await client.GetAsync(path);

        HttpAssert.Status(HttpStatusCode.NotFound, response);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("fg-error-code", body, StringComparison.Ordinal);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        if (mediaType is null)
        {
            Assert.Empty(body);
        }
    }

    [Fact]
    public async Task A_failure_on_a_page_shows_the_error_page_with_a_request_id()
    {
        using var client = server.CreateClient();
        using var response = await client.GetAsync(ErrorPageServerFixture.ThrowingPage);

        HttpAssert.Status(HttpStatusCode.InternalServerError, response);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains(">500<", page, StringComparison.Ordinal);
        Assert.Contains("Request id", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Provoked by the error page tests", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_on_a_protocol_path_is_a_plain_text_500()
    {
        using var client = server.CreateClient();
        using var response = await client.GetAsync(ErrorPageServerFixture.ThrowingProtocolPath);

        HttpAssert.Status(HttpStatusCode.InternalServerError, response);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Provoked by the error page tests", body, StringComparison.Ordinal);
    }
}
