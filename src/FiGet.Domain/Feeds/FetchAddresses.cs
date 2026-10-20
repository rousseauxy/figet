using System.Net;
using System.Net.Sockets;

namespace FiGet.Domain.Feeds;

/// <summary>
/// Which addresses this server will make an outgoing request to: an asset fetched by URL, a change-report webhook, an
/// upstream gallery typed as an address. A rule about addresses alone, so the fetchers and the rules that check a URL
/// before it is stored judge it the same way.
/// </summary>
public static class FetchAddresses
{
    /// <summary>
    /// Whether an address may be fetched from. Public addresses always; private ones only when allowed; the
    /// link-local block never, because that is where cloud metadata services answer.
    /// </summary>
    public static bool IsAllowed(IPAddress address, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254)
            {
                return false;
            }

            if (b[0] == 0 || b[0] >= 224)
            {
                return false;
            }

            // Reserved for documentation, benchmarks and protocol assignments (RFC 5737, RFC 2544, RFC 6890): nothing
            // legitimate answers there, and a fetch that resolves to one is a name pointing somewhere it should not.
            if ((b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                || (b[0] == 198 && b[1] is 18 or 19)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113))
            {
                return false;
            }

            var isPrivate = b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return !isPrivate || allowPrivateNetworks;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // IPv6 forms that carry an IPv4 address inside them - NAT64 (64:ff9b::/96) and 6to4 (2002::/16) -
            // are judged by that address, or 64:ff9b::a9fe:a9fe would reach the metadata service.
            var v6 = address.GetAddressBytes();
            if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6[4..12].All(x => x == 0))
            {
                return IsAllowed(new IPAddress(v6[12..16]), allowPrivateNetworks);
            }

            if (v6[0] == 0x20 && v6[1] == 0x02)
            {
                return IsAllowed(new IPAddress(v6[2..6]), allowPrivateNetworks);
            }

            var isPrivate = IPAddress.IsLoopback(address) || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
            return !isPrivate || allowPrivateNetworks;
        }

        return false;
    }
}
