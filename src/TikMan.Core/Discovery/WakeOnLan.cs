using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TikMan.Core.Discovery;

/// <summary>Sends a Wake-on-LAN "magic packet" (6× 0xFF followed by the target MAC repeated 16 times)
/// as a UDP broadcast on the usual ports (9 and 7), both to the global broadcast and each interface's
/// directed broadcast so it reaches the right segment.</summary>
public static class WakeOnLan
{
    /// <summary>Builds and broadcasts a magic packet for the given MAC (any separator, or none).
    /// Returns false if the MAC isn't 6 hex bytes or the send failed.</summary>
    public static bool Send(string mac)
    {
        if (ParseMac(mac) is not { } bytes) return false;

        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int rep = 1; rep <= 16; rep++) Array.Copy(bytes, 0, packet, rep * 6, 6);

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            var targets = new List<IPAddress> { IPAddress.Broadcast };
            targets.AddRange(DirectedBroadcasts());
            bool sentAny = false;
            foreach (var addr in targets)
                foreach (var port in new[] { 9, 7 })
                {
                    try { udp.Send(packet, packet.Length, new IPEndPoint(addr, port)); sentAny = true; }
                    catch (SocketException) { /* one target/port failed – keep trying the rest */ }
                }
            return sentAny;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Parses a MAC address in any common form ("AA:BB:…", "AA-BB-…", "aabb…") to 6 bytes,
    /// or null when it isn't exactly six hex bytes.</summary>
    public static byte[]? ParseMac(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        var b = new byte[6];
        for (int i = 0; i < 6; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    /// <summary>Directed broadcast address of every up IPv4 interface (address OR inverted mask).</summary>
    private static IEnumerable<IPAddress> DirectedBroadcasts()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask.All(m => m == 0)) continue;
                var bcast = new byte[4];
                for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bcast);
            }
        }
    }
}
