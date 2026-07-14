using System.Net;
using System.Net.NetworkInformation;

namespace TikMan.Core.Discovery;

/// <summary>Traceroute for the physical-topology view: pings with rising TTLs until the target
/// answers, and returns the router IPs passed on the way. On a flat LAN most paths are a single hop
/// (the target itself); anything longer reveals the routers between us and the device – which is the
/// physical structure a scan can actually prove, as opposed to guessing at cabling.</summary>
public static class TraceRoute
{
    /// <summary>The routers between this machine and <paramref name="ip"/>, in order, excluding the
    /// target itself. Empty for a device on our own segment. Null when the target never answered –
    /// an offline device has no provable path at all.</summary>
    public static async Task<List<string>?> TraceAsync(string ip, int maxHops = 6, int timeoutMs = 600,
        CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip, out var target)) return null;
        var hops = new List<string>();
        var payload = new byte[8];
        int silent = 0;

        for (int ttl = 1; ttl <= maxHops && !ct.IsCancellationRequested; ttl++)
        {
            using var ping = new Ping();
            PingReply reply;
            try
            {
                reply = await ping.SendPingAsync(target, timeoutMs, payload, new PingOptions(ttl, true))
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 300), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception) { return null; } // no ICMP allowed at all – nothing provable
            switch (reply.Status)
            {
                case IPStatus.Success:
                    return hops;                       // arrived – the routers collected so far are the path
                case IPStatus.TtlExpired when reply.Address is not null:
                    hops.Add(reply.Address.ToString()); // a router on the way
                    silent = 0;
                    break;
                default:
                    // A silent hop. One can be a firewall that drops TTL-expired; two in a row means
                    // the rest of the path won't talk either – stop burning time on it.
                    if (++silent >= 2) return null;
                    break;
            }
        }
        return null; // ran out of hops without reaching the target
    }

    /// <summary>The default IPv4 gateway of this machine ("" when there is none) – the root of the
    /// physical view.</summary>
    public static string DefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString() ?? "";
        }
        catch (NetworkInformationException) { return ""; }
    }
}
