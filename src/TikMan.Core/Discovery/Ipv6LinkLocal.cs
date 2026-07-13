using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace TikMan.Core.Discovery;

/// <summary>Derives a device's EUI-64 link-local IPv6 address from its MAC and actively solicits it.
/// Devices that build their link-local from the MAC (MikroTik RouterOS and most embedded gear – but
/// not Windows/Android, which randomise the interface ID) can be confirmed online this way even when
/// they ignore the passive ff02::1 all-nodes solicitation, so a known device reliably shows its
/// IPv6. Windows only (uses scoped ICMPv6 echo).</summary>
[SupportedOSPlatform("windows")]
public static class Ipv6LinkLocal
{
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
