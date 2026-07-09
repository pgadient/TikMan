using System.Net;
using System.Net.NetworkInformation;

namespace TikMan.Core.Discovery;

/// <summary>Facts about the local host's network configuration.</summary>
public static class NetworkInfo
{
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
