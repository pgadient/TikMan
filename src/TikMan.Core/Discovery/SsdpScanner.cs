using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>SSDP / UPnP discovery (the "network devices" your OS shows). One multicast M-SEARCH to
/// 239.255.255.250:1900 and every TV, set-top box, media renderer, NAS and router answers with a
/// LOCATION URL; fetching that gives the device's own words for what it is – friendly name,
/// manufacturer, model, device type.
///
/// This is what finally names the boxes that give nothing else away: a Swisscom TV Box, a Sonos, a
/// Chromecast and a smart TV all share generic ODM OUIs (Arcadyan, Vestel …) and expose only a bare
/// web port, so neither the MAC nor the ports can place them. UPnP just asks them.</summary>
public static class SsdpScanner
{
    private static readonly IPAddress Multicast = IPAddress.Parse("239.255.255.250");
    private const int Port = 1900;

    private static readonly byte[] SearchRequest = Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        "ST: ssdp:all\r\n\r\n");

    /// <summary>What a device says about itself over UPnP. Any field may be empty.</summary>
    public sealed record SsdpInfo(string Ip, string FriendlyName, string Manufacturer, string ModelName,
        string DeviceType);

    /// <summary>Multicasts an M-SEARCH on every IPv4 interface, then fetches each responder's device
    /// description. Keyed by IP. Never throws – a blocked port or a dead description URL just means
    /// fewer answers.</summary>
    public static async Task<Dictionary<string, SsdpInfo>> DiscoverAsync(
        TimeSpan duration, CancellationToken ct = default)
    {
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // ip → LOCATION url
        try
        {
            await Task.WhenAll(LocalIPv4().Select(local => ListenAsync(local, duration, locations, ct)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* cancelled – keep whatever we already have */ }
        catch (SocketException) { /* no usable interface */ }

        var results = new Dictionary<string, SsdpInfo>(StringComparer.OrdinalIgnoreCase);
        var fetched = await Task.WhenAll(locations.Select(kv => DescribeAsync(kv.Key, kv.Value, ct)))
            .ConfigureAwait(false);
        foreach (var info in fetched)
            if (info is not null) results[info.Ip] = info;
        return results;
    }

    private static IEnumerable<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);

    /// <summary>Sends the M-SEARCH from one interface and collects the LOCATION of every responder
    /// until the time is up. Devices answer more than once; the first LOCATION per IP wins.</summary>
    private static async Task ListenAsync(IPAddress local, TimeSpan duration,
        Dictionary<string, string> locations, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(local, 0));
        try
        {
            udp.Ttl = 4;
            await udp.SendAsync(SearchRequest, SearchRequest.Length, new IPEndPoint(Multicast, Port))
                .ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; } // an ICMP port-unreachable from a previous send

                var text = Encoding.ASCII.GetString(reply.Buffer);
                var location = Header(text, "LOCATION");
                if (location.Length == 0) continue;

                var ip = reply.RemoteEndPoint.Address.ToString();
                lock (locations)
                    if (!locations.ContainsKey(ip)) locations[ip] = location;
            }
        }
        catch (SocketException) { /* this interface can't multicast – skip it */ }
    }

    /// <summary>Fetches and parses a UPnP device description (the LOCATION XML).</summary>
    private static async Task<SsdpInfo?> DescribeAsync(string ip, string location, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var xml = await http.GetStringAsync(location, ct).ConfigureAwait(false);
            var info = new SsdpInfo(ip,
                Tag(xml, "friendlyName"), Tag(xml, "manufacturer"),
                Tag(xml, "modelName"), Tag(xml, "deviceType"));
            return info.FriendlyName.Length + info.Manufacturer.Length + info.ModelName.Length > 0
                ? info : null;
        }
        catch (Exception) { return null; } // unreachable / not XML / timed out – no description, no problem
    }

    private static string Header(string response, string name)
    {
        var m = Regex.Match(response, $@"^{name}\s*:\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string Tag(string xml, string name)
    {
        var m = Regex.Match(xml, $@"<{name}>(.*?)</{name}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }
}
