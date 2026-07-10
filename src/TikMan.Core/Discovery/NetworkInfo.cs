using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TikMan.Core.Discovery;

/// <summary>A local IPv4 network the host is attached to, as a CIDR plus the adapter it came from.</summary>
public readonly record struct LocalSubnet(string Cidr, string Adapter, string HostAddress);

/// <summary>Facts about the local host's network configuration.</summary>
public static class NetworkInfo
{
    /// <summary>All local IPv4 networks (one per up adapter with an IPv4 address), as CIDR strings
    /// derived from the real subnet mask – used to pre-fill and cycle the scan target.</summary>
    public static List<LocalSubnet> GetLocalSubnets()
    {
        var list = new List<LocalSubnet>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    int prefix = ua.PrefixLength is >= 16 and <= 32 ? ua.PrefixLength : 24;
                    var cidr = $"{NetworkAddress(ua.Address, prefix)}/{prefix}";
                    if (seen.Add(cidr)) list.Add(new LocalSubnet(cidr, nic.Name, ua.Address.ToString()));
                }
            }
        }
        catch (NetworkInformationException) { /* best effort */ }
        return list;
    }

    private static string NetworkAddress(IPAddress ip, int prefix)
    {
        var b = ip.GetAddressBytes();
        uint addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        uint net = addr & mask;
        return $"{(net >> 24) & 0xFF}.{(net >> 16) & 0xFF}.{(net >> 8) & 0xFF}.{net & 0xFF}";
    }

    /// <summary>The default-gateway IP addresses of every up interface (IPv4 and IPv6),
    /// as normalised strings — used to flag the gateway row in the device list.</summary>
    public static HashSet<string> GetDefaultGateways()
    {
        var gateways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                {
                    if (gw?.Address is not { } addr) continue;
                    if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any)) continue;
                    gateways.Add(addr.ToString());
                }
            }
        }
        catch (NetworkInformationException) { /* best effort – no gateway highlighting then */ }
        return gateways;
    }
}
