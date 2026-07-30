using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using TikMan.Core.Models;

namespace TikMan.Core.Discovery;

/// <summary>IPv6 host discovery on the local link. A full scan is impossible (the address
/// space is astronomically large), so instead we solicit responders by pinging the
/// all-nodes multicast address (ff02::1) and then read the OS Neighbor-Discovery cache,
/// parsed locale-independently. MNDP additionally finds MikroTik over IPv6.
/// <para>⚠️ Cross-platform: the ND cache is read with <c>netsh</c> on Windows, <c>ndp -an</c> on macOS and
/// <c>ip -6 neigh</c> on Linux. This used to be Windows-only (netsh), which is why the Avalonia client and
/// the headless host showed a permanently empty IPv6 view on a Mac even when the machine had a public IPv6 –
/// the poke worked, but nothing read the neighbours back.</para></summary>
public static partial class Ipv6Discovery
{
    // Accepts both separators: netsh prints "aa-bb-…", ndp/ip print "aa:bb:…".
    [GeneratedRegex(@"^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$")]
    private static partial Regex MacRegex();

    public static async Task<List<DiscoveredDevice>> DiscoverAsync(
        IProgress<DiscoveredDevice>? onFound = null, CancellationToken ct = default)
    {
        await PokeAllNodesAsync(ct).ConfigureAwait(false); // solicit neighbours so the cache fills
        var results = new List<DiscoveredDevice>();
        foreach (var (ip, mac) in await ReadNeighboursAsync(ct).ConfigureAwait(false))
        {
            var device = new DiscoveredDevice { IpAddress = ip, MacAddress = mac, Source = "ND" };
            results.Add(device);
            onFound?.Report(device);
        }
        return results;
    }

    /// <summary>Continuously discovers IPv6 neighbours for the given duration: the ND cache fills in
    /// gradually as neighbours reply to the ff02::1 solicitations, so a single pass misses hosts.
    /// Repeats poke + read every ~1.2 s and reports each newly seen neighbour once. Returns the total.</summary>
    public static async Task<int> DiscoverContinuousAsync(
        TimeSpan duration, IProgress<DiscoveredDevice> onFound, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long endTick = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (true)
        {
            await PokeAllNodesAsync(ct).ConfigureAwait(false);
            foreach (var (ip, mac) in await ReadNeighboursAsync(ct).ConfigureAwait(false))
            {
                if (!seen.Add(ip)) continue;
                onFound.Report(new DiscoveredDevice { IpAddress = ip, MacAddress = mac, Source = "ND" });
            }
            if (ct.IsCancellationRequested || Environment.TickCount64 >= endTick) break;
            await Task.Delay(1200, ct).ConfigureAwait(false);
        }
        return seen.Count;
    }

    /// <summary>Pings ff02::1 on every up IPv6 interface so neighbours reply and get cached.</summary>
    private static async Task PokeAllNodesAsync(CancellationToken ct)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (!nic.Supports(NetworkInterfaceComponent.IPv6)) continue;
            try
            {
                var target = IPAddress.Parse("ff02::1");
                target.ScopeId = nic.GetIPProperties().GetIPv6Properties().Index;
                using var ping = new Ping();
                await ping.SendPingAsync(target, 500).ConfigureAwait(false);
            }
            catch { /* interface without IPv6 / multicast blocked – best effort */ }
        }
    }

    /// <summary>The command that dumps the OS neighbour cache, per platform. All three print one neighbour
    /// per line with an IPv6 token and (when resolved) a MAC token, so one token-type parser reads them all.
    /// <para>⚠️ The Unix tools are resolved by trying known absolute paths first, then the bare name: a
    /// GUI-launched app often has a minimal <c>PATH</c> that omits <c>/usr/sbin</c> or <c>/sbin</c>, and the
    /// location varies by distro (<c>ip</c> lives in <c>/sbin</c>, <c>/usr/sbin</c> or <c>/bin</c> depending
    /// on the system). Bare name last, so a system with it only on PATH still works.</para></summary>
    private static (string File, string Args) NeighbourCommand() =>
        OperatingSystem.IsWindows() ? ("netsh", "interface ipv6 show neighbors")
        : OperatingSystem.IsMacOS() ? (ResolveTool("ndp", "/usr/sbin/ndp"), "-an")
        : (ResolveTool("ip", "/sbin/ip", "/usr/sbin/ip", "/bin/ip", "/usr/bin/ip"), "-6 neigh show");

    /// <summary>First existing absolute candidate, else the bare name (found via PATH).</summary>
    private static string ResolveTool(string bareName, params string[] candidates)
    {
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return bareName;
    }

    /// <summary>Reads the ND neighbour cache, extracting (IPv6, MAC) pairs by token type so it works
    /// regardless of the tool's output format or the display language.</summary>
    private static async Task<List<(string Ip, string Mac)>> ReadNeighboursAsync(CancellationToken ct)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (file, args) = NeighbourCommand();
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return list;
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            foreach (var line in output.Split('\n'))
            {
                string? ip = null, mac = null;
                foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    // ⚠️ Strip a scope suffix first: ndp prints link-local as "fe80::1%en0", which
                    // IPAddress.TryParse rejects because the zone is a name, not a number.
                    var t = token;
                    var pct = t.IndexOf('%');
                    if (pct >= 0) t = t[..pct];

                    if (ip is null && IPAddress.TryParse(t, out var addr) &&
                        addr.AddressFamily == AddressFamily.InterNetworkV6 && !addr.IsIPv6Multicast)
                        ip = addr.ToString();
                    else if (mac is null && MacRegex().IsMatch(token))
                        mac = token.ToUpperInvariant().Replace('-', ':');
                }
                if (ip is not null && mac is not null && seen.Add(ip))
                    list.Add((ip, mac));
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // The tool is missing or not on PATH – return whatever we have rather than failing the scan.
        }
        return list;
    }
}
