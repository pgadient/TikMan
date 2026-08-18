using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace TikMan.Core.Discovery;

/// <summary>Derives a device's EUI-64 link-local IPv6 address from its MAC and actively solicits it.
/// Devices that build their link-local from the MAC (MikroTik RouterOS and most embedded gear – but
/// not Windows/Android, which randomise the interface ID) can be confirmed online this way even when
/// they ignore the passive ff02::1 all-nodes solicitation, so a known device reliably shows its
/// IPv6. The active solicit is Windows only (uses scoped ICMPv6 echo); the address derivations are pure.</summary>
public static class Ipv6LinkLocal
{
    /// <summary>The MAC encoded in an EUI-64 link-local (the reverse of <see cref="FromMac"/>), uppercase
    /// "AA:BB:CC:DD:EE:FF", or "" when the address isn't an EUI-64 fe80:: (no <c>ff:fe</c> in the middle – a
    /// randomised/privacy interface ID has none). The universal/local bit is flipped back, so the result is
    /// the device's actual hardware MAC. Fills in the MAC of a host only ever seen by name/IP (mDNS, a stale
    /// ARP entry) that still carries an EUI-64 link-local. Restricted to fe80:: on purpose: a link-local is
    /// always the on-segment interface's MAC, whereas a global EUI-64 address could belong to another NIC.</summary>
    public static string ToMac(string ipv6)
    {
        if (!IPAddress.TryParse(ipv6, out var ip) || !ip.IsIPv6LinkLocal) return "";
        var b = ip.GetAddressBytes();
        if (b.Length != 16 || b[11] != 0xff || b[12] != 0xfe) return "";
        return $"{(byte)(b[8] ^ 0x02):X2}:{b[9]:X2}:{b[10]:X2}:{b[13]:X2}:{b[14]:X2}:{b[15]:X2}";
    }

    /// <summary>The EUI-64 link-local (fe80::…) for a MAC, or null when the MAC isn't six hex bytes.</summary>
    public static IPAddress? FromMac(string mac)
    {
        var parts = mac.Split(':', '-');
        if (parts.Length != 6) return null;
        var b = new byte[6];
        for (int i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, null, out b[i])) return null;

        var addr = new byte[16];
        addr[0] = 0xfe; addr[1] = 0x80;      // fe80::/64
        addr[8] = (byte)(b[0] ^ 0x02);       // flip the universal/local bit
        addr[9] = b[1]; addr[10] = b[2];
        addr[11] = 0xff; addr[12] = 0xfe;    // insert ff:fe in the middle
        addr[13] = b[3]; addr[14] = b[4]; addr[15] = b[5];
        return new IPAddress(addr);
    }

    /// <summary>Pings the EUI-64 link-local for the MAC on every up IPv6 interface and returns the
    /// address (without scope) when a reply comes back, else null.</summary>
    [SupportedOSPlatform("windows")]
    public static async Task<string?> SolicitAsync(string mac, CancellationToken ct = default)
    {
        var ll = FromMac(mac);
        if (ll is null) return null;
        var raw = ll.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ct.IsCancellationRequested) break;
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.Supports(NetworkInterfaceComponent.IPv6)) continue;
            try
            {
                var scoped = new IPAddress(raw, nic.GetIPProperties().GetIPv6Properties().Index);
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(scoped, 800).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) return ll.ToString();
            }
            catch { /* interface without IPv6 / unreachable – try the next */ }
        }
        return null;
    }
}
