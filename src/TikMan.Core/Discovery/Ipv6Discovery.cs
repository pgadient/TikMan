using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using TikMan.Core.Models;

namespace TikMan.Core.Discovery;

/// <summary>IPv6 host discovery on the local link. A full scan is impossible (the address
/// space is astronomically large), so instead we solicit responders by pinging the
/// all-nodes multicast address (ff02::1) and then read the OS Neighbor-Discovery cache
/// (netsh), parsed locale-independently. MNDP additionally finds MikroTik over IPv6.
/// A dedicated raw ND listener (needs admin/raw sockets) could be added later.</summary>
[SupportedOSPlatform("windows")]
public static partial class Ipv6Discovery
{
    [GeneratedRegex(@"^([0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}$")]
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

    /// <summary>Reads the ND neighbour cache via netsh, extracting (IPv6, MAC) pairs by token
    /// type so it works regardless of the Windows display language.</summary>
    private static async Task<List<(string Ip, string Mac)>> ReadNeighboursAsync(CancellationToken ct)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo("netsh", "interface ipv6 show neighbors")
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
                    if (ip is null && IPAddress.TryParse(token, out var addr) &&
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
            // netsh unavailable – return whatever we have
        }
        return list;
    }
}
