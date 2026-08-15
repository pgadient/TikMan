using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TikMan.Core.Models;

namespace TikMan.Core.Discovery;

/// <summary>Ubiquiti Device Discovery Protocol: UDP broadcast on port 10001. A single probe makes every
/// UniFi/Ubiquiti device on the segment announce itself — the login-free analogue of MikroTik's MNDP and
/// Zyxel's ZON. The reply names the device (model, firmware, hostname, MAC, IP, uptime).
///
/// <para>⚠️ Everything in a reply is UNTRUSTED wire data. <see cref="ParsePacket"/> is written to that
/// assumption: every field length is validated against the buffer before it is read, the item count is
/// capped, strings are length-bounded and control-stripped, and nothing in the packet is ever treated as
/// anything but data. A malformed or hostile packet yields null or a partial record — never an over-read
/// or an unbounded allocation.</para>
///
/// <para>⚠️ TLV type codes are from the reverse-engineered public spec; the exact model/firmware fields want
/// a pin against a real device (see the smoke fixture). Unknown types are skipped.</para>
///
/// <para>⚠️ Reality check (measured directly against a real USW-Lite-16-PoE, UniFi 7.0.50): a modern UniFi
/// switch DOES answer this classic broadcast probe – a 215-byte TLV reply carrying its MAC, model
/// ("USW-Lite-16-PoE") and uptime – but <b>intermittently</b>: <c>mcad</c> rate-limits/dedupes, so roughly
/// every other probe gets a reply and some are silent. (An earlier note here claimed it never answers; that
/// was a flawed measurement – it answers the BROADCAST, not a unicast, and only sometimes.) So the UBNT source
/// and badge are real when they appear, they just don't appear on every scan. The device is found and named
/// regardless via the subnet sweep + Ubiquiti OUI + the SSH read (<see cref="TikMan.Core.Api.UnifiSsh"/>);
/// this scanner is the login-free bonus that also catches older airMAX/UniFi gear.</para></summary>
public static class UbntDiscoveryScanner
{
    private const int UbntPort = 10001;

    // The classic v1 discovery probe. Devices reply to the sender with a TLV announcement.
    private static readonly byte[] Probe = { 0x01, 0x00, 0x00, 0x00 };

    // Belt-and-braces caps so a hostile packet can never make us loop or allocate unreasonably.
    private const int MaxItems = 128;
    private const int MaxStringBytes = 256;

    // How often to re-broadcast the probe within the listen window. ⚠️ Measured against live gear (a UniFi
    // switch + AP): a device answers a discovery broadcast AT MOST about ONCE PER ~10 s, regardless of how
    // fast we probe – blasting does NOT get more replies (it may even harden the rate-limit). What DOES matter
    // is that a fresh probe is available shortly after the device's ~10 s timer refills, so ~1 probe/second is
    // plenty. The real lever for catching every device is the WINDOW LENGTH, not the probe rate (see the
    // 14 s window in FleetService): the first reply from a given device can land anywhere in that ~10 s cycle.
    private const int ProbeIntervalMs = 1000;

    /// <param name="onlyLocalAddress">This machine's IPv4 on the interface to probe, or null/empty for all.
    /// Bound to that address so replies from other segments aren't collected (same rule as MNDP/ZON).</param>
    public static async Task<List<DiscoveredDevice>> DiscoverAsync(
        TimeSpan timeout,
        IProgress<DiscoveredDevice>? onFound = null,
        CancellationToken ct = default,
        string? onlyLocalAddress = null)
    {
        var results = new Dictionary<string, DiscoveredDevice>();  // key: MAC or IP

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        var bindTo = onlyLocalAddress is { Length: > 0 } && IPAddress.TryParse(onlyLocalAddress, out var only)
            ? only : IPAddress.Any;
        udp.Client.Bind(new IPEndPoint(bindTo, UbntPort));

        var localAddresses = GetLocalAddresses();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // ⚠️ Send the probe REPEATEDLY across the whole window, not once – a device rate-limits its replies to
        // ~one per 10 s and phases them arbitrarily, so a single probe (or a short window) catches only whichever
        // device happens to be due. Re-probing every ProbeIntervalMs keeps a fresh probe available as each
        // device's timer refills; the WINDOW (14 s, in FleetService) is what guarantees every device gets at
        // least one reply into it. Concurrent send + receive on one UDP socket is fine; the receive loop below
        // dedupes by MAC/IP, so a device is still reported exactly once no matter how many probes it answered.
        var targets = GetBroadcastTargets(onlyLocalAddress).ToList();
        var probing = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    foreach (var target in targets)
                    {
                        try { await udp.SendAsync(Probe, Probe.Length, new IPEndPoint(target, UbntPort)).ConfigureAwait(false); }
                        catch (SocketException) { /* interface without broadcast support – ignore */ }
                        catch (ObjectDisposedException) { return; }   // socket closed on cancellation
                    }
                    try { await Task.Delay(ProbeIntervalMs, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            catch (OperationCanceledException) { /* window elapsed */ }
        }, cts.Token);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                if (packet.Buffer.Length <= 4) continue;                    // our own probe echoed back
                if (localAddresses.Contains(packet.RemoteEndPoint.Address)) continue;

                var device = ParsePacket(packet.Buffer, packet.RemoteEndPoint.Address);
                if (device is null) continue;

                var key = device.MacAddress.Length > 0 ? device.MacAddress : device.IpAddress;
                if (results.ContainsKey(key)) continue;

                results[key] = device;
                onFound?.Report(device);
            }
        }
        catch (SocketException) { /* socket closed on cancellation */ }

        try { await probing.ConfigureAwait(false); } catch { /* best-effort: the probe loop is done anyway */ }

        return results.Values.OrderBy(d => d.IpAddress, StringComparer.Ordinal).ToList();
    }

    /// <summary>Parses a Ubiquiti discovery reply (header: version, command, 2-byte length; then TLV items,
    /// each type(1) + length(2, big-endian) + value). Pure and bounds-checked, so the smoke test can pin it
    /// against captured bytes. Returns null for a foreign/empty/hostile packet.</summary>
    public static DiscoveredDevice? ParsePacket(byte[] data, IPAddress sender)
    {
        if (data is null || data.Length < 4) return null;

        var device = new DiscoveredDevice
        {
            IpAddress = sender.ToString(),
            Source = "UBNT",
        };

        string modelDesignation = "", platform = "", fwFull = "", fwVersion = "";
        bool anyField = false;

        int offset = 4;                        // header: version(1) + command(1) + payload length(2)
        int items = 0;
        while (offset + 3 <= data.Length && items++ < MaxItems)
        {
            int type = data[offset];
            int len = (data[offset + 1] << 8) | data[offset + 2];
            offset += 3;
            if (len < 0 || offset + len > data.Length) break;   // ⚠️ never read past the buffer

            switch (type)
            {
                case 0x01 when len == 6:                        // MAC
                    if (device.MacAddress.Length == 0) device.MacAddress = Mac(data, offset);
                    anyField = true;
                    break;
                case 0x02 when len >= 10:                       // MAC (6) + IP (4) of an interface
                    if (device.MacAddress.Length == 0) device.MacAddress = Mac(data, offset);
                    var ip = new IPAddress(new[] { data[offset + 6], data[offset + 7], data[offset + 8], data[offset + 9] });
                    if (!ip.Equals(IPAddress.Any)) device.IpAddress = ip.ToString();
                    anyField = true;
                    break;
                case 0x03: fwFull = Str(data, offset, len); anyField = true; break;       // full firmware id
                case 0x0A when len == 4:                                                   // uptime (seconds)
                    device.Uptime = TimeSpan.FromSeconds(((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
                                                          | ((uint)data[offset + 2] << 8) | data[offset + 3]);
                    anyField = true;
                    break;
                case 0x0B: device.Identity = Str(data, offset, len); anyField = true; break;   // hostname
                case 0x0C: platform = Str(data, offset, len); anyField = true; break;          // platform / hw code
                case 0x15: modelDesignation = Str(data, offset, len); anyField = true; break;  // model designation
                case 0x16: fwVersion = Str(data, offset, len); anyField = true; break;         // firmware version
                // 0x06 username, 0x07 salt, 0x08 challenge, 0x0D essid, 0x1C ssh port, … : deliberately not
                // consumed. They're either sensitive (creds/crypto) or not needed for identification.
            }
            offset += len;
        }

        // Prefer the friendly model designation; fall back to the platform code. Firmware: the version field,
        // else the full identifier. All already length-bounded by Str().
        device.Board = modelDesignation.Length > 0 ? modelDesignation : platform;
        device.Platform = platform;
        device.Version = fwVersion.Length > 0 ? fwVersion : fwFull;

        return anyField ? device : null;
    }

    private static string Mac(byte[] data, int offset) =>
        string.Join(":", Enumerable.Range(offset, 6).Select(i => data[i].ToString("X2")));

    // UTF-8, length-bounded (a hostile length was already clamped to the buffer; this caps the string too),
    // control characters stripped so a crafted name can't smuggle nulls/escapes into a log or the UI.
    private static string Str(byte[] data, int offset, int len)
    {
        var take = Math.Min(len, MaxStringBytes);
        var s = Encoding.UTF8.GetString(data, offset, take);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsControl(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    // --- broadcast/interface helpers (kept local so this scanner touches no existing file) ---------------

    private static List<IPAddress> GetBroadcastTargets(string? only = null)
    {
        bool single = only is { Length: > 0 };
        // ⚠️ Always include the global 255.255.255.255 broadcast, even in single-interface mode. A device can
        // sit on a different /24 than this machine (measured: host on .15.x, UniFi gear on .10.x) – the
        // interface's own subnet-directed broadcast may not reach it, but the limited broadcast does. Without
        // it, single mode could probe an address no device ever hears.
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var (address, mask) in GetLocalIPv4WithMasks())
        {
            if (single && address.ToString() != only) continue;
            var addrBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var bcast = new byte[4];
            for (int i = 0; i < 4; i++) bcast[i] = (byte)(addrBytes[i] | ~maskBytes[i]);
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
