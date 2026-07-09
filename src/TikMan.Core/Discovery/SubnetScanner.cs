using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TikMan.Core.Models;

namespace TikMan.Core.Discovery;

/// <summary>Classic subnet scan: ping sweep, then port probe on responding hosts.
/// Also finds non-MikroTik devices; port 8291 (Winbox) is a strong indicator of a MikroTik.</summary>
public static class SubnetScanner
{
    /// <summary>Ports that are checked on each reachable host.</summary>
    public static readonly int[] DefaultPorts = { 443, 80, 8291, 8728, 22 };

    public static async Task<List<DiscoveredDevice>> ScanAsync(
        string targets,
        IProgress<DiscoveredDevice>? onFound = null,
        IProgress<int>? onHostScanned = null,
        CancellationToken ct = default)
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
                var device = await ProbeHostAsync(ip, ct).ConfigureAwait(false);
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

    private static async Task<DiscoveredDevice?> ProbeHostAsync(IPAddress ip, CancellationToken ct)
    {
        using var ping = new Ping();
        PingReply reply;
        try { reply = await ping.SendPingAsync(ip, 700).ConfigureAwait(false); }
        catch (PingException) { return null; }
        ct.ThrowIfCancellationRequested();
        if (reply.Status != IPStatus.Success) return null;

        var openPorts = new List<int>();
        foreach (var port in DefaultPorts)
        {
            if (await IsPortOpenAsync(ip, port, ct).ConfigureAwait(false))
                openPorts.Add(port);
        }

        return new DiscoveredDevice
        {
            IpAddress = ip.ToString(),
            Source = "Scan",
            OpenPorts = openPorts,
            IsLikelyMikroTik = openPorts.Contains(8291) || openPorts.Contains(8728),
        };
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(600);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
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
