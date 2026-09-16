using FiGet.Infrastructure.Reports;

namespace FiGet.Unit.Tests;

/// <summary>
/// The rules the webhook sender exists to keep: where it may post, and what it may say about where it posts. Both are
/// the kind of thing that stays true only while something checks.
/// </summary>
public sealed class ChangeWebhookTests
{
    /// <summary>The token lives in the path of a Teams or Power Automate URL, so the path is the credential.</summary>
    private const string SecretUrl = "https://hooks.example.test/workflows/abcdef123456/triggers/manual/paths/invoke?sig=TOPSECRETVALUE";

    [Fact]
    public void No_url_means_no_webhook()
    {
        using var webhook = new HttpChangeWebhook(new ChangeWebhookSettings());

        Assert.False(webhook.Configured);
        Assert.Equal("", webhook.Host);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://hooks.example.test/x")]
    [InlineData("not a url")]
    public void Only_http_and_https_are_posted_to(string url)
    {
        using var webhook = new HttpChangeWebhook(new ChangeWebhookSettings { Url = url });

        Assert.False(webhook.Configured);
    }

    /// <summary>What a page or a log line may say: enough to recognise the receiver, never enough to use it.</summary>
    [Fact]
    public void The_host_is_all_that_is_shown_of_the_url()
    {
        using var webhook = new HttpChangeWebhook(new ChangeWebhookSettings { Url = SecretUrl });

        Assert.Equal("https://hooks.example.test", webhook.Host);
        Assert.DoesNotContain("TOPSECRETVALUE", webhook.Host, StringComparison.Ordinal);
        Assert.DoesNotContain("workflows", webhook.Host, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure must not carry the URL back. The framework's own exception messages contain the request URI, which
    /// is exactly how a secret ends up in a log; this asserts the sender writes its own words instead.
    /// </summary>
    [Fact]
    public async Task A_failure_names_the_host_and_never_the_path()
    {
        // Nothing listens on this port, so the attempt fails without anything to leak it to.
        using var webhook = new HttpChangeWebhook(new ChangeWebhookSettings
        {
            Url = "http://127.0.0.1:1/hooks/abcdef123456?sig=TOPSECRETVALUE",
            AllowPrivateNetworks = true,
            Timeout = TimeSpan.FromSeconds(5),
        });

        var result = await webhook.PostAsync("{}", TestContext.Current.CancellationToken);

        Assert.False(result.Delivered);
        Assert.Equal("http://127.0.0.1:1", result.Host);
        Assert.NotNull(result.Problem);
        Assert.DoesNotContain("TOPSECRETVALUE", result.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456", result.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", result.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A private address is refused unless the deployment said so, and the cloud metadata address is refused either
    /// way: a webhook is a request this server makes, so it is the same risk the asset fetcher guards against.
    /// </summary>
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data", true)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("http://127.0.0.1:9/hook", false)]
    [InlineData("http://10.0.0.5:9/hook", false)]
    public async Task An_address_this_server_does_not_post_to_is_refused(string url, bool allowPrivate)
    {
        using var webhook = new HttpChangeWebhook(new ChangeWebhookSettings
        {
            Url = url,
            AllowPrivateNetworks = allowPrivate,
            Timeout = TimeSpan.FromSeconds(5),
        });

        var result = await webhook.PostAsync("{}", TestContext.Current.CancellationToken);

        Assert.False(result.Delivered);
        Assert.Contains("does not post to", result.Problem ?? "", StringComparison.Ordinal);
    }
}
