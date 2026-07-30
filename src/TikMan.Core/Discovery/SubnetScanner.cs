using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TikMan.Core.Models;

namespace TikMan.Core.Discovery;

/// <summary>Classic subnet scan: ping sweep, then port probe on responding hosts.
/// Also finds non-MikroTik devices; port 8291 (Winbox) is a strong indicator of a MikroTik.</summary>
public static class SubnetScanner
{
    /// <summary>TCP service ports probed on each reachable host, with a display name.
    /// UDP-only services (tftp/dns-udp/snmp/syslog) can't be detected by a TCP connect and are
    /// intentionally omitted; sftp shares port 22 with ssh, and bind (DNS) shares 53.</summary>
    public static readonly (int Port, string Name)[] ServicePorts =
    {
        (21, "ftp"), (22, "ssh"), (23, "telnet"), (25, "smtp"), (53, "dns"),
        (80, "http"), (135, "wmi"), (139, "netbios"), (143, "imap"), (443, "https"), (445, "smb"), (465, "smtps"),
        // Printing (LPD/IPP/JetDirect), RTSP and SIP: these say outright what a device *is* – a copier,
        // a camera, a phone – where a bare web port says nothing. Without them everything with a web UI
        // ended up looking like a server.
        (515, "lpd"), (554, "rtsp"), (587, "submission"), (631, "ipp"), (873, "rsync"), (990, "ftps"),
        (993, "imaps"),
        (623, "ipmi"),               // out-of-band management controller (iRMC / iLO / iDRAC / BMC)
        (3389, "rdp"), (5060, "sip"), (5900, "vnc"), (5901, "vnc"),
        (8080, "http-alt"), (8291, "winbox"), (8728, "api"), (8729, "api-ssl"), (9100, "jetdirect"),
        (16992, "amt"), (16993, "amt"), // Intel AMT / vPro management
    };

    // SNMP (UDP 161) can't be found by the TCP connect scan, so it's detected out-of-band with a
    // real SNMP GET (see SnmpProbe) and only its name is registered here for the badge.
    private static readonly Dictionary<int, string> PortNames =
        ServicePorts.Append((Port: 161, Name: "snmp")).ToDictionary(p => p.Port, p => p.Name);

    /// <summary>Service name for a port, or the number as text if unknown.</summary>
    public static string ServiceName(int port) =>
        PortNames.TryGetValue(port, out var name) ? name : port.ToString();

    public static async Task<List<DiscoveredDevice>> ScanAsync(
        string targets,
        IProgress<DiscoveredDevice>? onFound = null,
        IProgress<int>? onHostScanned = null,
        CancellationToken ct = default,
        int pingTimeoutMs = 600,
        int pingRetries = 0,
        bool scanUnpingable = false)
    {
        var addresses = EnumerateTargets(targets);
        var results = new List<DiscoveredDevice>();
        var resultLock = new object();
        using var limiter = new SemaphoreSlim(64);

        var tasks = addresses.Select(async ip =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var device = await ProbeHostAsync(ip, ct, pingTimeoutMs, pingRetries, scanUnpingable)
                    .ConfigureAwait(false);
                if (device is not null)
                {
                    lock (resultLock) results.Add(device);
                    onFound?.Report(device);
                }
            }
            finally
            {
                limiter.Release();
                onHostScanned?.Report(1);
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .OrderBy(d => IpSortKey(d.IpAddress))
            .ToList();
    }

    public static int CountHosts(string targets) => EnumerateTargets(targets).Count;

    /// <summary>Extra ports the user asked for, on top of <see cref="ServicePorts"/>. Set per scan from
    /// the settings; empty by default.</summary>
    private static int[] _customPorts = Array.Empty<int>();

    /// <summary>The custom ports currently in force – the badge layer asks so it can tell a user-added
    /// port from one of the built-in services.</summary>
    public static IReadOnlyList<int> CustomPorts => _customPorts;

    /// <summary>Parses a comma-separated port list ("8006, 9000, 32400"). Junk entries are dropped rather
    /// than rejecting the whole list: a stray character should not silently disable the setting.
    /// <para>Ports already in <see cref="ServicePorts"/> are skipped – scanning them twice would only
    /// double the traffic, and they already have a proper service badge.</para></summary>
    public static int[] ParseCustomPorts(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<int>();

        var builtIn = ServicePorts.Select(p => p.Port).ToHashSet();
        return spec.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.Trim(), out var p) ? p : -1)
            .Where(p => p is > 0 and <= 65535 && !builtIn.Contains(p))
            .Distinct()
            .Take(64)   // a sanity bound: this multiplies every host's probe count
            .ToArray();
    }

    public static void SetCustomPorts(string spec) => _customPorts = ParseCustomPorts(spec);

    /// <summary>Ports tried on a host that did not answer a ping, when the user has enabled that. A short
    /// list on purpose: it only has to establish "something is listening here", after which the full sweep
    /// runs. Five probes per dead address instead of thirty-one is the difference between a usable option
    /// and one that floods the network – on a /21 that is ~10k connects instead of ~63k.</summary>
    private static readonly int[] TripwirePorts = { 80, 443, 22, 445, 3389 };

    /// <summary>Bounds how many TCP connects are in flight <b>across the whole scan</b>.
    ///
    /// <para>⚠️ The per-host limiter does not bound this. Each live host fans out to all
    /// <see cref="ServicePorts"/> at once, so 64 hosts × 31 ports is ~2000 sockets – measured at 827 in
    /// practice, with 411 simultaneously in SYN_SENT. Four things go wrong at that scale:</para>
    /// <list type="bullet">
    /// <item><b>File descriptors.</b> macOS ships a soft limit of 256 open files for GUI apps; a scan that
    /// wants hundreds of sockets simply starts failing, and the failures look like "host has no open
    /// ports" rather than like an error.</item>
    /// <item><b>Ephemeral ports.</b> Every completed connect holds its local port in TIME_WAIT for two
    /// minutes. The Windows dynamic range is 16384 ports; a large or continuously repeating scan can walk
    /// through it, after which connects fail with AddressAlreadyInUse – again as silent false negatives.</item>
    /// <item><b>Intrusion detection.</b> Hundreds of concurrent half-open connections from one host is the
    /// textbook signature of a port scan, because that is what it is. On a monitored network that gets the
    /// machine flagged or quarantined.</item>
    /// <item><b>The far end.</b> Small embedded devices – printers, IoT, old switches – have tiny accept
    /// backlogs and can drop traffic or wedge under a simultaneous burst.</item>
    /// </list>
    /// <para>So the gate counts <i>sockets</i>, not hosts. Default 256, and lower on macOS to stay under
    /// that descriptor limit.</para></summary>
    private static SemaphoreSlim _socketGate = new(DefaultSocketLimit);

    private static int DefaultSocketLimit => OperatingSystem.IsMacOS() ? 96 : 256;

    /// <summary>Sets the in-flight connect limit. 0 restores the platform default.</summary>
    public static void SetSocketLimit(int limit)
    {
        var value = limit > 0 ? Math.Clamp(limit, 8, 2048) : DefaultSocketLimit;
        var old = Interlocked.Exchange(ref _socketGate, new SemaphoreSlim(value));
        old.Dispose();
    }

    /// <summary>The current limit, for the settings UI to show what is actually in force.</summary>
    public static int SocketLimit => _socketGate.CurrentCount;

    private static async Task<DiscoveredDevice?> ProbeHostAsync(IPAddress ip, CancellationToken ct,
        int pingTimeoutMs, int pingRetries, bool scanUnpingable)
    {
        // A single echo is easily lost on a busy segment, which makes the found-host count wobble
        // (52 vs 53). Retry a few times (configurable) before writing the host off. Total attempts =
        // 1 + retries; clamped to sane bounds so a bad config can't hang the scan.
        int attempts = Math.Clamp(pingRetries, 0, 10) + 1;
        int timeout = Math.Clamp(pingTimeoutMs, 100, 5000);
        using var ping = new Ping();
        bool alive = false;
        for (int attempt = 0; attempt < attempts && !alive; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { alive = (await ping.SendPingAsync(ip, timeout).ConfigureAwait(false)).Status == IPStatus.Success; }
            catch (PingException) { /* transient – retry */ }
        }

        // ⚠️ ICMP is a hard gate by default, and plenty of hosts block it – Windows does out of the box.
        // Those devices are invisible to this scan unless one of the discovery protocols (MNDP, mDNS,
        // SSDP, ZON) names them. The option below trades traffic for finding them anyway.
        if (!alive)
        {
            if (!scanUnpingable) return null;
            alive = await AnyPortOpenAsync(ip, TripwirePorts, ct).ConfigureAwait(false);
            if (!alive) return null;
        }

        // Probe the service ports in parallel (only reached for hosts that answered), plus whatever extra
        // ports the user configured.
        var wanted = ServicePorts.Select(sp => sp.Port).Concat(_customPorts);
        var probes = wanted.Select(async port => (Port: port, Open: await IsPortOpenAsync(ip, port, ct).ConfigureAwait(false)));
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        var openPorts = results.Where(r => r.Open).Select(r => r.Port).OrderBy(p => p).ToList();

        // Enrich the bare IP with the MAC (from the ARP cache, populated by the ping above) and a
        // hostname (reverse DNS). The vendor is then resolved from the MAC in the view.
        var mac = ResolveMacAddress(ip);
        var identity = await ReverseDnsAsync(ip, ct).ConfigureAwait(false);

        return new DiscoveredDevice
        {
            IpAddress = ip.ToString(),
            Source = "Scan",
            OpenPorts = openPorts,
            IsLikelyMikroTik = openPorts.Contains(8291) || openPorts.Contains(8728),
            MacAddress = mac,
            Identity = identity,
        };
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref uint macAddrLen);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        public byte mac0, mac1, mac2, mac3, mac4, mac5, mac6, mac7; // bPhysAddr[MAXLEN_PHYSADDR]
        public int dwAddr;
        public int dwType; // 3 = dynamic, 4 = static
    }

    /// <summary>Resolves the MAC of an on-link IPv4 host; "" when the platform can't say. Windows:
    /// an active ARP request first, then the OS ARP table (SendARP fails with error 67 while the
    /// neighbour entry is Probe/Stale, but the scan's ping has usually populated the table already).
    /// Linux: the kernel neighbour table (/proc/net/arp) – the ping that preceded this call is what
    /// fills it. macOS: the same table via <c>arp -an</c>, which is the only way to read it without
    /// linking against the BSD routing socket.</summary>
    public static string ResolveMacAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return "";
        if (OperatingSystem.IsWindows())
        {
            var mac = SendArpMac(ip);
            return mac.Length > 0 ? mac : ArpTableMac(ip);
        }
        if (OperatingSystem.IsLinux()) return ProcArpMac(ip);
        if (OperatingSystem.IsMacOS()) return BsdArpMac(ip);
        return "";
    }

    /// <summary>macOS neighbour table, read by shelling out to <c>arp -an</c>. There is no /proc, and the
    /// alternative is P/Invoking the BSD routing socket – far more code for the same answer.</summary>
    private static string BsdArpMac(IPAddress ip)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/sbin/arp", "-an")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return "";
            var output = proc.StandardOutput.ReadToEnd();
            // Bounded: a hung arp must not stall the scan for this one host.
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } return ""; }
            return ParseBsdArp(output, ip.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Picks one host's MAC out of <c>arp -an</c> output. Pure, so it can be pinned by tests.
    /// <para>Lines look like:
    /// <code>? (192.168.1.1) at ac:de:48:0:11:22 on en0 ifscope [ethernet]</code></para>
    /// <para>⚠️ Two traps. BSD <c>arp</c> prints octets <b>without leading zeros</b> – "0:11:22", not
    /// "00:11:22" – so a fixed-length check (which is what the Linux path uses) rejects every second
    /// address; each octet has to be padded back. And an entry that never resolved reads
    /// "<c>at (incomplete) on en0</c>", which is not a MAC at all.</para></summary>
    public static string ParseBsdArp(string arpOutput, string ip)
    {
        var needle = "(" + ip + ")";
        foreach (var line in arpOutput.Split('\n'))
        {
            if (!line.Contains(needle, StringComparison.Ordinal)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var at = Array.IndexOf(parts, "at");
            if (at < 0 || at + 1 >= parts.Length) continue;

            var candidate = parts[at + 1];
            if (candidate.StartsWith("(", StringComparison.Ordinal)) continue;   // "(incomplete)"

            var octets = candidate.Split(':');
            if (octets.Length != 6) continue;
            if (!octets.All(o => o.Length is 1 or 2 &&
                                 o.All(c => Uri.IsHexDigit(c)))) continue;

            var mac = string.Join(":", octets.Select(o => o.PadLeft(2, '0').ToUpperInvariant()));
            return mac == "00:00:00:00:00:00" ? "" : mac;
        }
        return "";
    }

    /// <summary>Linux neighbour table: one line per entry, "IP … Flags HWaddress … Device". An
    /// incomplete entry carries the all-zero MAC, which is as good as no answer.</summary>
    private static string ProcArpMac(IPAddress ip)
    {
        try
        {
            var target = ip.ToString();
            foreach (var line in File.ReadLines("/proc/net/arp").Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && parts[0] == target &&
                    parts[3].Length == 17 && parts[3] != "00:00:00:00:00:00")
                    return parts[3].ToUpperInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "";
    }

    private static string SendArpMac(IPAddress ip)
    {
        try
        {
            int dest = BitConverter.ToInt32(ip.GetAddressBytes(), 0);
            var mac = new byte[6];
            uint len = (uint)mac.Length;
            if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return "";
            return string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
        }
        catch (DllNotFoundException) { return ""; }
        catch (EntryPointNotFoundException) { return ""; }
    }

    /// <summary>Looks the IP up in the OS ARP/neighbour table (GetIpNetTable) – has the MAC even when
    /// an active SendARP resolution just failed, as long as the entry is still cached from the ping.</summary>
    private static string ArpTableMac(IPAddress ip)
    {
        int target = BitConverter.ToInt32(ip.GetAddressBytes(), 0);
        int size = 0;
        try
        {
            GetIpNetTable(IntPtr.Zero, ref size, false); // ask for the required buffer size
            if (size <= 0) return "";
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buffer, ref size, false) != 0) return "";
                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<MIB_IPNETROW>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_IPNETROW>(IntPtr.Add(buffer, 4 + i * rowSize));
                    if (row.dwAddr == target && row.dwPhysAddrLen >= 6 && row.dwType is 3 or 4)
                    {
                        var b = new[] { row.mac0, row.mac1, row.mac2, row.mac3, row.mac4, row.mac5 };
                        if (b.Any(x => x != 0)) return string.Join(":", b.Select(x => x.ToString("X2")));
                    }
                }
                return "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (DllNotFoundException) { return ""; }
        catch (EntryPointNotFoundException) { return ""; }
    }

    /// <summary>Reverse-DNS lookup for a hostname (short form), with a 1s timeout; "" if none.</summary>
    private static async Task<string> ReverseDnsAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1000);
            var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(timeout.Token).ConfigureAwait(false);
            var host = entry.HostName;
            int dot = host.IndexOf('.');
            return dot > 0 ? host[..dot] : host;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return ""; } // timeout / no PTR record / DNS unavailable
    }

    /// <summary>True as soon as any of <paramref name="ports"/> accepts – used as a liveness check for a
    /// host that did not answer ICMP. Sequential on purpose: most of these addresses are empty, and the
    /// point is to spend as little as possible confirming that.</summary>
    private static async Task<bool> AnyPortOpenAsync(IPAddress ip, int[] ports, CancellationToken ct)
    {
        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsPortOpenAsync(ip, port, ct).ConfigureAwait(false)) return true;
        }
        return false;
    }

    /// <summary>One TCP connect attempt. Every socket in the scan passes through
    /// <see cref="_socketGate"/> – see its remarks for why the count matters.</summary>
    private static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, CancellationToken ct)
    {
        var gate = _socketGate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000); // a busy file server can be slow to accept – 600ms missed some
            try
            {
                await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
        }
        // ⚠️ Release the semaphore this call took, not whatever _socketGate points at now – the limit can
        // be changed mid-scan, and releasing the new one would hand out a permit that was never taken.
        finally { gate.Release(); }
    }

    private const int MaxHosts = 65536;

    /// <summary>Parses a comma-separated target spec into host addresses. Each part may be a
    /// CIDR subnet ("192.168.1.0/24"), an IP range ("192.168.1.50-192.168.1.100" or the short
    /// "192.168.1.50-100"), or a single IP ("192.168.1.42").</summary>
    public static List<IPAddress> EnumerateTargets(string spec)
    {
        var seen = new HashSet<uint>();
        var result = new List<IPAddress>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ip in EnumeratePart(part))
            {
                if (seen.Add(ToUInt(ip))) result.Add(ip);
                if (result.Count > MaxHosts)
                    throw new ArgumentException($"Too many addresses (> {MaxHosts}) – narrow the range.");
            }
        }
        if (result.Count == 0)
            throw new ArgumentException("No valid targets – e.g. 192.168.1.0/24, 192.168.1.50-100 or 192.168.1.42");

        result.Sort((a, b) => ToUInt(a).CompareTo(ToUInt(b)));
        return result;
    }

    private static IEnumerable<IPAddress> EnumeratePart(string part)
    {
        if (part.Contains('/')) return EnumerateCidr(part);
        if (part.Contains('-')) return EnumerateRange(part);
        if (IPAddress.TryParse(part, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            return new[] { ip };
        throw new ArgumentException($"Invalid target: \"{part}\"");
    }

    private static List<IPAddress> EnumerateRange(string part)
    {
        int dash = part.IndexOf('-');
        var startStr = part[..dash].Trim();
        var endStr = part[(dash + 1)..].Trim();
        if (!IPAddress.TryParse(startStr, out var startIp) || startIp.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"Invalid range start: \"{startStr}\"");

        uint start = ToUInt(startIp);
        uint end;
        // NOTE: a bare number like "52" must be treated as a last-octet short form, NOT parsed as
        // an IP — IPAddress.TryParse("52") would (wrongly) yield 0.0.0.52 and create a huge range.
        if (endStr.Contains('.'))
        {
            if (!IPAddress.TryParse(endStr, out var endIp) || endIp.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException($"Invalid range end: \"{endStr}\"");
            end = ToUInt(endIp);
        }
        else if (byte.TryParse(endStr, out var lastOctet))
        {
            end = (start & 0xFFFFFF00u) | lastOctet; // short form: only the last octet
        }
        else
        {
            throw new ArgumentException($"Invalid range end: \"{endStr}\"");
        }

        if (end < start) (start, end) = (end, start);
        long count = (long)end - start + 1;
        if (count > MaxHosts)
            throw new ArgumentException($"Range too large ({count} addresses, max {MaxHosts}) – narrow it down.");

        var list = new List<IPAddress>((int)count);
        for (uint a = start; a <= end; a++) list.Add(FromUInt(a));
        return list;
    }

    /// <summary>Parses "192.168.1.0/24" and returns all host addresses (excluding network/broadcast).</summary>
    private static List<IPAddress> EnumerateCidr(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) ||
            baseIp.AddressFamily != AddressFamily.InterNetwork || !int.TryParse(parts[1], out var prefix))
            throw new ArgumentException($"Invalid subnet: \"{cidr}\" – expected e.g. 192.168.1.0/24");

        if (prefix is < 16 or > 32)
            throw new ArgumentException("Prefix must be between /16 and /32 (larger ranges take too long).");

        uint baseAddr = ToUInt(baseIp);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint network = baseAddr & mask;
        uint broadcast = network | ~mask;

        var hosts = new List<IPAddress>();
        if (prefix >= 31)
        {
            for (uint a = network; a <= broadcast; a++) hosts.Add(FromUInt(a));
        }
        else
        {
            for (uint a = network + 1; a < broadcast; a++) hosts.Add(FromUInt(a));
        }
        return hosts;
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    private static uint IpSortKey(string ip) =>
        IPAddress.TryParse(ip, out var parsed) ? ToUInt(parsed) : uint.MaxValue;
}
