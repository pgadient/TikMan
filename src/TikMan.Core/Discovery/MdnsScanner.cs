using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TikMan.Core.Discovery;

/// <summary>mDNS / DNS-SD ("Bonjour") discovery. Devices announce what they *are* on 224.0.0.251:5353
/// – their hostname, the services they offer, and, for Apple gear, an exact hardware model.
///
/// This is the only thing that can separate the Apple devices, and it does it outright: an iPhone, an
/// iPad, a HomePod and an Apple TV all share one Apple OUI and would otherwise be indistinguishable.
/// Over mDNS they say "iPhone15,2", "iPad13,8", "AudioAccessory5,1", "AppleTV6,2". It equally names
/// Chromecasts, Sonos, printers and NAS boxes, needs no router access and no credentials, and works
/// on any network.</summary>
public static class MdnsScanner
{
    private static readonly IPAddress Multicast = IPAddress.Parse("224.0.0.251");
    private const int Port = 5353;

    // The service types worth asking for. "_services._dns-sd._udp" is the meta-query that makes a
    // device list everything else it offers.
    private static readonly string[] Queries =
    {
        "_services._dns-sd._udp.local",
        "_device-info._tcp.local",      // Apple: the exact hardware model
        "_airplay._tcp.local", "_raop._tcp.local",        // AirPlay video / audio
        "_googlecast._tcp.local",       // Chromecast, Android TV
        "_spotify-connect._tcp.local", "_sonos._tcp.local",
        "_hap._tcp.local",              // HomeKit accessory
        "_ipp._tcp.local", "_ipps._tcp.local", "_printer._tcp.local", "_pdl-datastream._tcp.local",
        "_uscan._tcp.local", "_uscans._tcp.local",   // AirScan / eSCL scanning
        "_smb._tcp.local", "_afpovertcp._tcp.local", "_adisk._tcp.local",
        "_workstation._tcp.local", "_ssh._tcp.local", "_http._tcp.local",
    };

    /// <summary>What a device told us about itself over mDNS. AirPrint is recognised by the URF key
    /// in a printer's TXT record (mandatory for AirPrint, absent from plain IPP printers); AirScan by
    /// the _uscan/_uscans (eSCL) service.</summary>
    public sealed record MdnsInfo(string Ip, string HostName, string Model, IReadOnlyList<string> Services,
        bool AirPrint = false, bool AirScan = false);

    /// <summary>Queries every IPv4 interface and collects the answers, keyed by the responder's IP.
    /// Records are credited to the host that sent them, which is exactly right: over mDNS a device
    /// answers for itself.</summary>
    public static async Task<Dictionary<string, MdnsInfo>> DiscoverAsync(
        TimeSpan duration, CancellationToken ct = default)
    {
        var hosts = new Dictionary<string, Host>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.WhenAll(LocalIPv4().Select(local => ListenAsync(local, duration, hosts, ct)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* keep what we have */ }
        catch (SocketException) { /* no usable interface */ }

        return hosts.ToDictionary(
            kv => kv.Key,
            kv => new MdnsInfo(kv.Key, kv.Value.Name, kv.Value.Model,
                               kv.Value.Services.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                               kv.Value.AirPrint,
                               kv.Value.Services.Contains("_uscan._tcp") || kv.Value.Services.Contains("_uscans._tcp")),
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Host
    {
        public string Name = "";
        public string Model = "";
        public bool AirPrint;
        public readonly HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Print/scan service instances whose TXT we still have to ask for.</summary>
        public readonly List<string> PendingTxt = new();
    }

    private static IEnumerable<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.SupportsMulticast &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);

    private static async Task ListenAsync(IPAddress local, TimeSpan duration,
        Dictionary<string, Host> hosts, CancellationToken ct)
    {
        using var udp = new UdpClient();
        try
        {
            // Port 5353 is usually already held by the OS's own Bonjour responder, so share it rather
            // than fight it – that is what mDNS expects of every participant on the host.
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(local, Port));
            udp.JoinMulticastGroup(Multicast, local);
        }
        catch (SocketException)
        {
            return; // this interface won't do multicast – the others still might
        }

        try
        {
            async Task SendAllAsync()
            {
                foreach (var q in Queries)
                {
                    var packet = BuildQuery(q);
                    await udp.SendAsync(packet, packet.Length, new IPEndPoint(Multicast, Port)).ConfigureAwait(false);
                }
            }
            await SendAllAsync().ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);

            // Ask a second time midway: a device that has *just* answered someone suppresses the same
            // answer for about a second (RFC 6762 known-answer suppression), so a single shot misses
            // whatever spoke recently. The repeat lands after the suppression window.
            _ = Task.Delay(1500, cts.Token).ContinueWith(async _ =>
            {
                try { await SendAllAsync().ConfigureAwait(false); }
                catch (Exception) { /* socket already closed – the scan is over anyway */ }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
            var txtAsked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }

                if (reply.RemoteEndPoint.Address.Equals(local)) continue; // our own query coming back
                var ip = reply.RemoteEndPoint.Address.ToString();

                Host host;
                lock (hosts)
                {
                    if (!hosts.TryGetValue(ip, out host!)) hosts[ip] = host = new Host();
                    Absorb(reply.Buffer, host);
                }

                // A PTR answer names the service *instance*, but its TXT – where AirPrint's URF key
                // lives – often isn't volunteered alongside. Ask for it explicitly, once per instance.
                List<string>? wanted = null;
                lock (hosts)
                {
                    foreach (var inst in host.PendingTxt)
                        if (txtAsked.Add(inst)) (wanted ??= new List<string>()).Add(inst);
                    host.PendingTxt.Clear();
                }
                if (wanted is not null)
                    foreach (var inst in wanted)
                    {
                        var q = BuildQuery(inst, qtype: 16);
                        try { await udp.SendAsync(q, q.Length, new IPEndPoint(Multicast, Port)).ConfigureAwait(false); }
                        catch (SocketException) { /* interface hiccup – the sweep goes on */ }
                    }
            }
        }
        catch (SocketException) { /* interface went away mid-scan */ }
        finally
        {
            try { udp.DropMulticastGroup(Multicast); } catch { /* already gone */ }
        }
    }

    /// <summary>A standard mDNS question – a PTR query for a service type, or (qtype 16) a TXT query
    /// for one service instance.</summary>
    private static byte[] BuildQuery(string name, ushort qtype = 12)
    {
        var body = new List<byte>
        {
            0x00, 0x00,             // id (0 for mDNS)
            0x00, 0x00,             // flags: standard query
            0x00, 0x01,             // 1 question
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            body.Add((byte)label.Length);
            body.AddRange(Encoding.UTF8.GetBytes(label));
        }
        body.Add(0x00);
        body.Add((byte)(qtype >> 8)); body.Add((byte)qtype);
        body.AddRange(new byte[] { 0x00, 0x01 }); // QCLASS = IN
        return body.ToArray();
    }

    // ---- parsing ------------------------------------------------------------------------------

    /// <summary>Pulls the hostname, the offered services and (Apple's) hardware model out of one
    /// response and folds them into what we already know about that host.</summary>
    private static void Absorb(byte[] buf, Host host)
    {
        try
        {
            if (buf.Length < 12) return;
            int qd = (buf[4] << 8) | buf[5];
            int total = ((buf[6] << 8) | buf[7]) + ((buf[8] << 8) | buf[9]) + ((buf[10] << 8) | buf[11]);

            int pos = 12;
            for (int i = 0; i < qd && pos < buf.Length; i++)
            {
                ReadName(buf, ref pos);
                pos += 4; // qtype + qclass
            }

            for (int i = 0; i < total && pos + 10 <= buf.Length; i++)
            {
                var name = ReadName(buf, ref pos);
                if (pos + 10 > buf.Length) return;
                int type = (buf[pos] << 8) | buf[pos + 1];
                int len = (buf[pos + 8] << 8) | buf[pos + 9];
                pos += 10;
                if (pos + len > buf.Length) return;
                int end = pos + len;

                switch (type)
                {
                    case 1 when len == 4:            // A – the record's name is the hostname
                        if (host.Name.Length == 0) host.Name = StripLocal(name);
                        break;

                    case 12:                         // PTR – "…_airplay._tcp.local" → an offered service
                        AddService(host, name);
                        var target = pos;
                        var instance = ReadName(buf, ref target);
                        AddService(host, instance);
                        // Printing/scanning instances get a follow-up TXT query: that record carries
                        // the URF key that says "AirPrint", and it is rarely volunteered unasked.
                        if (instance.Contains("._ipp", StringComparison.OrdinalIgnoreCase) ||
                            instance.Contains("._uscan", StringComparison.OrdinalIgnoreCase))
                            host.PendingTxt.Add(instance);
                        break;

                    case 33:                         // SRV – its owner name carries the service type
                        AddService(host, name);
                        break;

                    case 16:                         // TXT – Apple's exact hardware model lives here
                        AddService(host, name);
                        ReadTxt(buf, pos, end, host);
                        break;
                }
                pos = end;
            }
        }
        catch (Exception) { /* a malformed packet is not worth a stack trace – just take the next one */ }
    }

    /// <summary>Reduces a record name to the service type it belongs to. A real type is exactly two
    /// labels – "_airplay._tcp" – so both the bare PTR owner ("_airplay._tcp") and a service instance
    /// ("Bett-Pascal._airplay._tcp") land on the same thing, and the meta-query
    /// ("_services._dns-sd._udp") and stray fragments are dropped rather than logged as services.</summary>
    private static void AddService(Host host, string name)
    {
        var labels = StripLocal(name).Split('.', StringSplitOptions.RemoveEmptyEntries);
        int first = Array.FindIndex(labels, l => l.StartsWith('_'));
        if (first < 0) return;

        var svc = labels[first..];
        if (svc.Length != 2 || svc[0].Length < 2) return;
        if (svc[1] is not ("_tcp" or "_udp")) return;
        host.Services.Add($"{svc[0]}.{svc[1]}");
    }

    private static void ReadTxt(byte[] buf, int pos, int end, Host host)
    {
        while (pos < end && pos < buf.Length)
        {
            int len = buf[pos++];
            if (len == 0 || pos + len > end) break;
            var entry = Encoding.UTF8.GetString(buf, pos, len);
            pos += len;

            foreach (var key in new[] { "model=", "am=", "md=" })
                if (entry.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    OfferModel(host, entry[key.Length..].Trim());

            // The URF key (Apple's raster format) is mandatory for AirPrint and absent from plain IPP
            // printers – its presence alone is the AirPrint capability.
            if (entry.StartsWith("URF=", StringComparison.OrdinalIgnoreCase)) host.AirPrint = true;
        }
    }

    /// <summary>Keeps the most telling model string seen for a host. Apple publishes two: the AirPlay
    /// records carry the marketing model ("AudioAccessory5,1", "AppleTV6,2"), while _device-info
    /// carries the bare board id ("B520AP", "J305AP") – and a board id says nothing about whether the
    /// thing is a speaker or a TV box. The marketing model always has a comma in it, so it wins.</summary>
    private static void OfferModel(Host host, string value)
    {
        if (value.Length == 0) return;
        bool better = host.Model.Length == 0 ||
                      (!host.Model.Contains(',') && value.Contains(','));
        if (better) host.Model = value;
    }

    private static string StripLocal(string name) =>
        name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    /// <summary>Reads a DNS name, following the compression pointers that make the wire format compact
    /// (and that a naive reader walks straight off the end of).</summary>
    private static string ReadName(byte[] buf, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos, jumps = 0;
        bool jumped = false;

        while (p >= 0 && p < buf.Length)
        {
            int len = buf[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= buf.Length || ++jumps > 16) break; // truncated, or a pointer loop
                int offset = ((len & 0x3F) << 8) | buf[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = offset;
                continue;
            }
            p++;
            if (p + len > buf.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(buf, p, len));
            p += len;
        }

        if (!jumped) pos = p;
        return sb.ToString();
    }
}
