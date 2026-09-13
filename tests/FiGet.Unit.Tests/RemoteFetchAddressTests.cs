using System.Net;
using FiGet.Infrastructure.Assets;

namespace FiGet.Unit.Tests;

/// <summary>
/// Which addresses the server fetches a file from. This is the whole of the guard against the fetch being
/// used to read what only the server can reach, so every family of address it has to recognise is here.
/// </summary>
public sealed class RemoteFetchAddressTests
{
    [Theory]
    [InlineData("93.184.215.14")]
    [InlineData("2606:2800:21f:cb07:6820:80da:af6b:8b2c")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    public void A_public_address_is_always_allowed(string address)
    {
        Assert.True(HttpRemoteFileSource.IsAllowed(IPAddress.Parse(address), allowPrivateNetworks: false));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.100.10")]
    [InlineData("100.64.5.5")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("2002:c0a8:0101::1")]
    public void A_private_address_needs_private_networks_allowed(string address)
    {
        Assert.False(HttpRemoteFileSource.IsAllowed(IPAddress.Parse(address), allowPrivateNetworks: false));
        Assert.True(HttpRemoteFileSource.IsAllowed(IPAddress.Parse(address), allowPrivateNetworks: true));
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    public void Metadata_unspecified_and_multicast_addresses_are_never_allowed(string address)
    {
        Assert.False(HttpRemoteFileSource.IsAllowed(IPAddress.Parse(address), allowPrivateNetworks: true));
    }
}
