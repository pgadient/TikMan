using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SharpPcap;
using SharpPcap.LibPcap;
using TikMan.Core.Models;

namespace TikMan.Core.Discovery;

/// <summary>Zyxel Discovery Protocol (ZDP / the "ZON" utility) discovery. ZDP is a raw-Ethernet
/// protocol (EtherType 0xC1C1, no IP), so it needs Npcap; when that isn't installed the scan simply
/// returns nothing. It multicasts a discovery request to 01:A0:C5:11:11:11, then asks each responder
/// for its details, and parses the TLV replies for model, firmware, IPv4 and system name.</summary>
public static class ZdpScanner
{
    private static readonly byte[] Multicast = { 0x01, 0xA0, 0xC5, 0x11, 0x11, 0x11 };
    private static readonly byte[] EtherType = { 0xC1, 0xC1 };
    private const ushort MsgDiscover = 0x8000;   // broadcast "who's there"
    private const ushort MsgInfoRequest = 0x0002; // targeted "tell me your details"

    // TLV attribute ids seen in the ZON exchange.
    private const byte AttrList = 0x02, AttrMac = 0x03, AttrModel = 0x04, AttrFirmware = 0x05,
                       AttrIpv4 = 0x07, AttrName = 0x16;

    // The exact field set the ZON utility asks for in its info request. Some devices (the XGS1930
    // switch) only reply with all fields – including firmware – when the request matches this set.
    private static readonly byte[] InfoRequestAttrs =
        { 0x21, AttrModel, AttrFirmware, AttrIpv4, 0x2a, 0x30, 0x2f, AttrName, 0x18, 0x27, 0x2d };

    private static bool? _available;

    /// <summary>True when the raw-capture layer (Npcap, with WinPcap API-compatible mode) is usable –
    /// i.e. ZON discovery can run. Cached after the first check.</summary>
    public static bool IsAvailable()
    {
        if (_available is { } v) return v;
        try { _ = CaptureDeviceList.Instance.Count; _available = true; }
        catch { _available = false; }
        return _available.Value;
    }

    /// <summary>The installed Npcap/libpcap version string (e.g. "Npcap version 1.79, based on libpcap
    /// version 1.10.4"), or null when the capture layer isn't available.</summary>
    public static string? NpcapVersion()
    {
        try { return Pcap.Version; }
        catch { return null; }
    }

    public static async Task<List<DiscoveredDevice>> DiscoverAsync(
        TimeSpan duration, IProgress<DiscoveredDevice>? onFound = null, CancellationToken ct = default)
    {
        var results = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Run(() => Scan(duration, results, onFound, ct), ct).ConfigureAwait(false);
        }
        catch (DllNotFoundException) { /* Npcap not installed – no ZDP discovery */ }
        catch (TypeInitializationException) { /* Npcap not installed */ }
        catch (PcapException) { /* capture layer unavailable */ }
        return results.Values.Where(d => d.IpAddress.Length > 0).ToList();
    }

    private static void Scan(TimeSpan duration, Dictionary<string, DiscoveredDevice> results,
        IProgress<DiscoveredDevice>? onFound, CancellationToken ct)
    {
        var adapters = CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>()
            .Where(d => d.MacAddress is not null &&
                        d.Interface.Addresses.Any(a => a.Addr?.ipAddress?.AddressFamily == AddressFamily.InterNetwork))
            .ToList();

        foreach (var dev in adapters)
        {
            if (ct.IsCancellationRequested) break;
            try { ScanAdapter(dev, duration, results, onFound, ct); }
            catch (PcapException) { /* this adapter failed – try the next */ }
            finally { try { if (dev.Opened) dev.Close(); } catch { /* ignore */ } }
        }
    }

    private static void ScanAdapter(LibPcapLiveDevice dev, TimeSpan duration,
        Dictionary<string, DiscoveredDevice> results, IProgress<DiscoveredDevice>? onFound, CancellationToken ct)
    {
        var srcMac = dev.MacAddress!.GetAddressBytes();
        dev.Open(new DeviceConfiguration { Mode = DeviceModes.Promiscuous, ReadTimeout = 200 });
        dev.Filter = "ether proto 0xc1c1";

        dev.SendPacket(BuildRequest(srcMac, Multicast, MsgDiscover, new byte[] { AttrList, AttrMac, AttrModel }));

        var queried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long endTick = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < endTick && !ct.IsCancellationRequested)
        {
            if (dev.GetNextPacket(out var capture) != GetPacketStatus.PacketRead) continue;
            var data = capture.GetPacket().Data;
            if (!Parse(data, out var info)) continue;

            var mac = info.Mac;
            if (mac.Length == 0) continue;
            var existing = results.TryGetValue(mac, out var d) ? d : new DiscoveredDevice { Source = "ZON", MacAddress = mac };
            if (info.Model.Length > 0) existing.Board = info.Model;
            if (info.Firmware.Length > 0) existing.Version = info.Firmware;
            if (info.Ip.Length > 0) existing.IpAddress = info.Ip;
            if (info.Name.Length > 0) existing.Identity = info.Name;
            results[mac] = existing;
            if (existing.IpAddress.Length > 0) onFound?.Report(existing);

            // Ask a freshly-seen device for its full details (IPv4/firmware/name) once.
            if (existing.IpAddress.Length == 0 && queried.Add(mac))
                dev.SendPacket(BuildRequest(srcMac, HexToMac(mac), MsgInfoRequest, InfoRequestAttrs));
        }
    }

    /// <summary>Builds a ZDP frame: Ethernet header + 34-byte ZDP header + one "length 0" TLV per
    /// requested attribute + a terminator.</summary>
    private static byte[] BuildRequest(byte[] srcMac, byte[] dstMac, ushort msgType, byte[] requestedAttrs)
    {
        var body = new List<byte>();
        body.AddRange(dstMac);
        body.AddRange(srcMac);
        body.AddRange(EtherType);
        // ZDP header
        body.AddRange(new byte[] { 0x00, 0x01 });                        // version
        body.Add((byte)(msgType >> 8)); body.Add((byte)(msgType & 0xFF)); // message type
        body.AddRange(srcMac);                                            // sender MAC
        body.AddRange(new byte[] { 0x10, 0x09, 0x27, 0x11 });            // transaction id
        body.AddRange(new byte[20]);                                     // reserved
        foreach (var attr in requestedAttrs) { body.Add(attr); body.Add(0x00); } // request TLVs (len 0)
        body.AddRange(new byte[] { 0x00, 0x00 });                        // terminator
        while (body.Count < 60) body.Add(0x00);                          // pad to the minimum Ethernet frame
        return body.ToArray();
    }

    /// <summary>Parses one raw ZDP frame into a device (public for testing/reuse); null if not ZDP.</summary>
    public static DiscoveredDevice? ParseFrame(byte[] frame) =>
        Parse(frame, out var i)
            ? new DiscoveredDevice { Source = "ZON", MacAddress = i.Mac, Board = i.Model, Version = i.Firmware, IpAddress = i.Ip, Identity = i.Name }
            : null;

    private readonly record struct ZdpInfo(string Mac, string Model, string Firmware, string Ip, string Name);

    private static bool Parse(byte[] frame, out ZdpInfo info)
    {
        info = default;
        // eth(14) + zdp header(34) = 48; need at least that plus a TLV.
        if (frame.Length < 50 || frame[12] != 0xC1 || frame[13] != 0xC1) return false;

        string mac = "", model = "", firmware = "", ip = "", name = "";
        int pos = 48; // first TLV
        while (pos + 2 <= frame.Length)
        {
            byte type = frame[pos];
            if (type == 0x00) break;
            int len = frame[pos + 1];
            int valStart = pos + 2;
            if (valStart + len > frame.Length) break;

            switch (type)
            {
                case AttrMac when len == 6:
                    mac = string.Join(":", frame.Skip(valStart).Take(6).Select(b => b.ToString("X2")));
                    break;
                case AttrIpv4 when len == 4:
                    ip = new IPAddress(frame[valStart..(valStart + 4)]).ToString();
                    break;
                case AttrModel: model = AsciiZ(frame, valStart, len); break;
                case AttrFirmware: firmware = AsciiZ(frame, valStart, len); break;
                case AttrName: name = AsciiZ(frame, valStart, len); break;
            }
            pos = valStart + len;
        }
        // Fall back to the Ethernet source MAC if the payload didn't carry attribute 0x03.
        if (mac.Length == 0)
            mac = string.Join(":", frame.Skip(6).Take(6).Select(b => b.ToString("X2")));
        info = new ZdpInfo(mac, model, firmware, ip, name);
        return true;
    }

    private static string AsciiZ(byte[] data, int start, int len)
    {
        int end = Array.IndexOf(data, (byte)0, start, len);
        if (end < 0) end = start + len;
        return Encoding.ASCII.GetString(data, start, end - start).Trim();
    }

    private static byte[] HexToMac(string mac)
    {
        var parts = mac.Split(':', '-');
        var b = new byte[6];
        for (int i = 0; i < 6 && i < parts.Length; i++) byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out b[i]);
        return b;
    }
}
