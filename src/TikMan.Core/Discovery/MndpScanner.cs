using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TikMan.Core.Models;

namespace TikMan.Core.Discovery;

/// <summary>MikroTik Neighbor Discovery Protocol: UDP broadcast on port 5678.
/// A single trigger packet makes every MikroTik on the LAN announce itself.</summary>
public static class MndpScanner
{
    private const int MndpPort = 5678;

    public static async Task<List<DiscoveredDevice>> DiscoverAsync(
        TimeSpan timeout,
        IProgress<DiscoveredDevice>? onFound = null,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, DiscoveredDevice>(); // Key: MAC or IP

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, MndpPort));

        var trigger = new byte[] { 0, 0, 0, 0 };
        foreach (var target in GetBroadcastTargets())
        {
            try { await udp.SendAsync(trigger, trigger.Length, new IPEndPoint(target, MndpPort)).ConfigureAwait(false); }
            catch (SocketException) { /* interface without broadcast support - ignore */ }
        }

        var localAddresses = GetLocalAddresses();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                if (packet.Buffer.Length <= 4) continue; // our own trigger packet
                if (localAddresses.Contains(packet.RemoteEndPoint.Address)) continue;

                var device = ParsePacket(packet.Buffer, packet.RemoteEndPoint.Address);
                if (device is null) continue;

                var key = device.MacAddress != "" ? device.MacAddress : device.IpAddress;
                if (results.ContainsKey(key)) continue;

                results[key] = device;
                onFound?.Report(device);
            }
        }
        catch (SocketException) { /* socket closed on cancellation */ }

        return results.Values.OrderBy(d => d.IpAddress, StringComparer.Ordinal).ToList();
    }

    private static DiscoveredDevice? ParsePacket(byte[] data, IPAddress sender)
    {
        var device = new DiscoveredDevice
        {
            IpAddress = sender.ToString(),
            Source = "MNDP",
            IsLikelyMikroTik = true,
        };

        try
        {
            int offset = 4; // header: type/version + sequence number
            bool anyField = false;
            while (offset + 4 <= data.Length)
            {
                int type = (data[offset] << 8) | data[offset + 1];
                int len = (data[offset + 2] << 8) | data[offset + 3];
                offset += 4;
                if (len < 0 || offset + len > data.Length) break;

                switch (type)
                {
                    case 1 when len == 6: // MAC
                        device.MacAddress = string.Join(":", data.Skip(offset).Take(6).Select(b => b.ToString("X2")));
                        anyField = true;
                        break;
                    case 5: device.Identity = Utf8(data, offset, len); anyField = true; break;
                    case 7: device.Version = Utf8(data, offset, len); anyField = true; break;
                    case 8: device.Platform = Utf8(data, offset, len); anyField = true; break;
                    case 10 when len == 4: // uptime, seconds little-endian
                        device.Uptime = TimeSpan.FromSeconds(BitConverter.ToUInt32(data, offset));
                        anyField = true;
                        break;
                    case 12: device.Board = Utf8(data, offset, len); anyField = true; break;
                }
                offset += len;
            }
            return anyField ? device : null;
        }
        catch
        {
            return null; // foreign/malformed packet
        }
    }

    private static string Utf8(byte[] data, int offset, int len) =>
        Encoding.UTF8.GetString(data, offset, len).Trim('\0');

    /// <summary>Global broadcast plus directed broadcasts per interface -
    /// otherwise 255.255.255.255 only goes out via the default interface.</summary>
    private static List<IPAddress> GetBroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var (address, mask) in GetLocalIPv4WithMasks())
        {
            var addrBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var bcast = new byte[4];
            for (int i = 0; i < 4; i++)
                bcast[i] = (byte)(addrBytes[i] | ~maskBytes[i]);
            targets.Add(new IPAddress(bcast));
        }
        return targets.Distinct().ToList();
    }

    private static HashSet<IPAddress> GetLocalAddresses() =>
        GetLocalIPv4WithMasks().Select(t => t.Address).ToHashSet();

    private static List<(IPAddress Address, IPAddress Mask)> GetLocalIPv4WithMasks()
    {
        var list = new List<(IPAddress, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask is null || ua.IPv4Mask.Equals(IPAddress.Any)) continue;
                list.Add((ua.Address, ua.IPv4Mask));
            }
        }
        return list;
    }
}
