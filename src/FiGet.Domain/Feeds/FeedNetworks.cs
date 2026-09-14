using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace FiGet.Domain.Feeds;

/// <summary>
/// The addresses a feed or asset directory may be reached from: none listed means any. A list is addresses and ranges in
/// CIDR notation, one per line (commas and semicolons separate too, as a form field is pasted into). Stored as the text
/// the administrator typed, normalised to one entry per line, and parsed again on each request: a list is a handful of
/// entries, and parsing them is cheaper than a cache that has to notice the row changed.
///
/// IPv4 and IPv6 are separate families: <c>127.0.0.1</c> does not admit <c>::1</c>, so a client that comes both ways is
/// listed twice. An IPv4 client on a dual-stack listener arrives as an IPv4-mapped IPv6 address and is compared as IPv4.
/// </summary>
public static class FeedNetworks
{
    /// <summary>What the column holds; forty or so ranges, which is more than a feed has reasons for.</summary>
    public const int MaxLength = 2000;

    private static readonly char[] Separators = ['\n', '\r', ',', ';', ' ', '\t'];

    /// <summary>Whether a request from the address may reach a feed with this list. No list: anyone. No address: nobody.</summary>
    public static bool Allows(string? allowedNetworks, IPAddress? address)
    {
        if (string.IsNullOrWhiteSpace(allowedNetworks))
        {
            return true;
        }

        if (address is null || !TryParse(allowedNetworks, out var networks, out _))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return networks.Any(network => network.Contains(address));
    }

    /// <summary>
    /// Parses a list. False with the entry that is not an address or a range, so the form can point at it. A range must
    /// start at its own boundary (<c>10.0.0.0/8</c>, not <c>10.0.0.1/8</c>): the other reading would silently mean
    /// something else than what was typed.
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyList<IPNetwork> networks, out string? invalid)
    {
        var parsed = new List<IPNetwork>();
        networks = parsed;
        invalid = null;
        foreach (var entry in Entries(text))
        {
            if (TryParseEntry(entry, out var network))
            {
                parsed.Add(network);
            }
            else
            {
                invalid = entry;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Strict on purpose. The runtime's parsers read <c>10.0.0</c> as an address and <c>10.0.0.1/8</c> as a range, and
    /// both would then quietly mean something other than what was typed.
    /// </summary>
    private static bool TryParseEntry(string entry, out IPNetwork network)
    {
        network = default;
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(addressText, out var address)
            || !string.Equals(address.ToString(), addressText, StringComparison.OrdinalIgnoreCase)
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (slash >= 0 && (!int.TryParse(entry.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > bits))
        {
            return false;
        }

        // Bits set past the prefix - 10.0.0.1/8 - are refused rather than masked off; the runtime does neither.
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bits; bit++)
        {
            if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0)
            {
                return false;
            }
        }

        network = new IPNetwork(address, prefix);
        return true;
    }

    /// <summary>The list as stored: one entry per line, or null when there is none. Call after <see cref="TryParse"/>.</summary>
    public static string? Normalise(string? text)
    {
        var entries = Entries(text).ToList();
        return entries.Count == 0 ? null : string.Join('\n', entries);
    }

    /// <summary>How many entries a stored list holds, for a title.</summary>
    public static int Count(string? allowedNetworks) => Entries(allowedNetworks).Count();

    private static IEnumerable<string> Entries(string? text) =>
        (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
