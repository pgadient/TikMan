namespace TikMan.Core.Discovery;

/// <summary>One equal slice of a network, as the address-distribution view shows it.</summary>
/// <param name="First">First address of the slice, as a 32-bit value – also the sort key.</param>
/// <param name="Last">Last address of the slice, inclusive.</param>
/// <param name="Cidr">"192.168.1.0/26" – the heading.</param>
/// <param name="Range">"192.168.1.0-192.168.1.63" – the same thing spelled out, because a prefix length is
/// arithmetic most people would rather not do in their head while looking at a map.</param>
public readonly record struct IpSegment(uint First, uint Last, string Cidr, string Range);

/// <summary>Splits a network into a handful of equal blocks so the address distribution can be read at a
/// glance. Pure arithmetic, no I/O – which is what makes it testable.</summary>
public static class IpSegments
{
    /// <summary>How many equal blocks a network is cut into, as a power of two.
    ///
    /// <para>⚠️ The count is chosen from the network's own size, not fixed. A flat "always 8" turns a /24
    /// into eight blocks of 32 addresses – finer than anyone thinks about a small LAN – while "always 4"
    /// gives a /21 four blocks of 512, which hides the /24 boundaries that are usually the whole point.
    /// The rule below reads as: <b>use 8 blocks while each one is still at least a /24, otherwise 4</b>.
    /// That lands a /21 on eight /24s and a /24 on four /26s.</para>
    ///
    /// <para>Blocks are always a power of two so each one is a real CIDR block with a printable prefix;
    /// five or six equal blocks would need a mask that does not exist.</para></summary>
    public static int SegmentPrefixFor(int prefix)
    {
        // Nothing to divide: below this the slices stop being addresses anyone assigns.
        if (prefix >= 27) return prefix;
        return prefix + 3 <= 24 ? prefix + 3 : prefix + 2;
    }

    /// <summary>The slices of "a.b.c.d/p", in address order. A single slice covering the whole network when
    /// it is too small to divide usefully; an empty list when the input is not a CIDR.</summary>
    public static List<IpSegment> Plan(string cidr)
    {
        var list = new List<IpSegment>();
        if (!TryParseCidr(cidr, out var network, out var prefix)) return list;

        var segPrefix = SegmentPrefixFor(prefix);
        // ⚠️ Sizes are computed in 64-bit. A /0 would make "1u << 32" undefined – on x86 the shift count
        // wraps to 0 and the size comes out as 1 instead of the whole address space.
        long total = 1L << (32 - prefix);
        long size = 1L << (32 - segPrefix);

        for (long offset = 0; offset < total; offset += size)
        {
            uint first = (uint)(network + offset);
            uint last = (uint)(network + offset + size - 1);
            list.Add(new IpSegment(first, last, $"{ToIp(first)}/{segPrefix}", $"{ToIp(first)}-{ToIp(last)}"));
        }
        return list;
    }

    /// <summary>The /24 an address sits in, as a CIDR – the fallback grouping for devices that belong to no
    /// known local network (a VPN peer, a device found on an adapter that has since gone away).</summary>
    public static string EnclosingSlash24(string ip) =>
        TryParseIp(ip, out var v) ? $"{ToIp(v & 0xFFFFFF00u)}/24" : "";

    public static bool TryParseIp(string ip, out uint value)
    {
        value = 0;
        var parts = (ip ?? "").Trim().Split('.');
        if (parts.Length != 4) return false;
        uint v = 0;
        foreach (var p in parts)
        {
            if (!byte.TryParse(p, out var b)) return false;
            v = (v << 8) | b;
        }
        value = v;
        return true;
    }

    public static bool TryParseCidr(string cidr, out uint network, out int prefix)
    {
        network = 0; prefix = 0;
        var parts = (cidr ?? "").Trim().Split('/');
        if (parts.Length != 2 || !TryParseIp(parts[0], out var ip)) return false;
        if (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32) return false;
        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        network = ip & mask;
        return true;
    }

    public static string ToIp(uint v) => $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
}
