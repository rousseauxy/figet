using System.Net;
using FiGet.Domain.Feeds;

namespace FiGet.Unit.Tests;

/// <summary>
/// Which addresses a feed's network list admits. One rule for the protocol endpoints and the pages, so this is the
/// whole of it: an empty list admits anyone, a list admits what it names and nothing else, and text that is not an
/// address is refused rather than read as something near it.
/// </summary>
public sealed class FeedNetworksTests
{
    [Theory]
    [InlineData(null, "203.0.113.9", true)]
    [InlineData("", "203.0.113.9", true)]
    [InlineData("  \n ", "203.0.113.9", true)]
    [InlineData("10.0.0.0/8", "10.20.30.40", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("10.0.0.0/8, 192.168.1.25", "192.168.1.25", true)]
    [InlineData("10.0.0.0/8\n192.168.1.25", "192.168.1.26", false)]
    [InlineData("192.168.1.25", "192.168.1.25", true)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("2001:db8::/32", "2001:db9::5", false)]
    [InlineData("::1", "::1", true)]
    // The families are separate: the IPv4 loopback does not admit the IPv6 one.
    [InlineData("127.0.0.1", "::1", false)]
    public void Admits_what_the_list_names(string? list, string address, bool expected)
    {
        Assert.Equal(expected, FeedNetworks.Allows(list, IPAddress.Parse(address)));
    }

    /// <summary>An IPv4 client on a dual-stack listener arrives as ::ffff:a.b.c.d and is compared as IPv4.</summary>
    [Fact]
    public void An_ipv4_mapped_address_is_compared_as_ipv4()
    {
        Assert.True(FeedNetworks.Allows("10.0.0.0/8", IPAddress.Parse("::ffff:10.1.2.3")));
        Assert.False(FeedNetworks.Allows("10.0.0.0/8", IPAddress.Parse("::ffff:11.1.2.3")));
    }

    [Fact]
    public void A_list_admits_nobody_without_an_address()
    {
        Assert.False(FeedNetworks.Allows("10.0.0.0/8", null));
        Assert.True(FeedNetworks.Allows(null, null));
    }

    [Theory]
    [InlineData("10.0.0.1/8", "10.0.0.1/8")]
    [InlineData("10.0.0.0/33", "10.0.0.0/33")]
    [InlineData("10.0.0.0/8; office", "office")]
    [InlineData("10.0.0", "10.0.0")]
    [InlineData("example.com", "example.com")]
    public void Refuses_text_that_is_not_an_address_and_names_it(string list, string expectedInvalid)
    {
        Assert.False(FeedNetworks.TryParse(list, out _, out var invalid));
        Assert.Equal(expectedInvalid, invalid);
        Assert.False(FeedNetworks.Allows(list, IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void Normalises_to_one_entry_per_line()
    {
        Assert.Equal("10.0.0.0/8\n192.168.1.25\n::1", FeedNetworks.Normalise(" 10.0.0.0/8, 192.168.1.25 ;\r\n\n::1 "));
        Assert.Null(FeedNetworks.Normalise(" , \n"));
        Assert.Equal(3, FeedNetworks.Count("10.0.0.0/8\n192.168.1.25\n::1"));
    }
}
