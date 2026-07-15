using System.Text;

namespace TikMan.Core.Discovery;

/// <summary>Reads a switch's forwarding table (MAC → port name) over SNMP – the vendor-neutral way to
/// prove which port a device hangs off, for the switches we hold no API credentials for (Zyxel & Co.,
/// or a MikroTik without a login). Read-only community suffices; "public" is the common default.
///
/// Tries the VLAN-aware Q-BRIDGE table (dot1qTpFdbPort) first – that is what managed switches fill –
/// and falls back to the classic BRIDGE-MIB (dot1dTpFdbPort). Bridge ports are resolved to interface
/// names via dot1dBasePortIfIndex and ifName/ifDescr, so the result reads "port 5" as "ether5"/"ge5",
/// same as the RouterOS REST path.</summary>
public static class SnmpFdb
{
    private static readonly int[] Dot1qTpFdbPort = { 1, 3, 6, 1, 2, 1, 17, 7, 1, 2, 2, 1, 2 };
    private static readonly int[] Dot1dTpFdbPort = { 1, 3, 6, 1, 2, 1, 17, 4, 3, 1, 2 };
    private static readonly int[] Dot1dBasePortIfIndex = { 1, 3, 6, 1, 2, 1, 17, 1, 4, 1, 2 };
    private static readonly int[] IfNameOid = { 1, 3, 6, 1, 2, 1, 31, 1, 1, 1, 1 };
    private static readonly int[] IfDescrOid = { 1, 3, 6, 1, 2, 1, 2, 2, 1, 2 };

    /// <summary>The forwarding table as MAC (twelve bare hex digits) → port name; null when the device
    /// doesn't answer SNMP or exposes no FDB.</summary>
    public static async Task<Dictionary<string, string>?> ReadAsync(
        string host, string community, CancellationToken ct = default)
    {
        // The FDB itself: OID index carries the MAC (its last six numbers), the value the bridge port.
        var rows = await SnmpProbe.WalkAsync(host, community, Dot1qTpFdbPort, 4096, ct).ConfigureAwait(false);
        if (rows is null || rows.Count == 0)
            rows = await SnmpProbe.WalkAsync(host, community, Dot1dTpFdbPort, 4096, ct).ConfigureAwait(false);
        if (rows is null || rows.Count == 0) return null;

        var macToBridgePort = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (oid, _, value) in rows)
        {
            var parts = oid.Split('.');
            if (parts.Length < 6) continue;
            var mac = new StringBuilder(12);
            bool ok = true;
            for (int i = parts.Length - 6; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var b) || b is < 0 or > 255) { ok = false; break; }
                mac.Append(b.ToString("X2"));
            }
            int port = IntOf(value);
            if (ok && port > 0 && !macToBridgePort.ContainsKey(mac.ToString()))
                macToBridgePort[mac.ToString()] = port;
        }
        if (macToBridgePort.Count == 0) return null;

        // Bridge port → ifIndex → interface name. Both lookups are best-effort: without them the map
        // still works, the ports are just called "port 5" instead of "ether5".
        var portToIf = new Dictionary<int, int>();
        foreach (var (oid, _, value) in await SnmpProbe.WalkAsync(host, community, Dot1dBasePortIfIndex, 512, ct)
                     .ConfigureAwait(false) ?? new())
            if (int.TryParse(oid.Split('.')[^1], out var bridgePort))
                portToIf[bridgePort] = IntOf(value);

        var ifNames = new Dictionary<int, string>();
        foreach (var source in new[] { IfNameOid, IfDescrOid })
        {
            foreach (var (oid, tag, value) in await SnmpProbe.WalkAsync(host, community, source, 512, ct)
                         .ConfigureAwait(false) ?? new())
                if (tag == 0x04 && int.TryParse(oid.Split('.')[^1], out var ifIndex) && !ifNames.ContainsKey(ifIndex))
                    ifNames[ifIndex] = Encoding.UTF8.GetString(value).Trim();
            if (ifNames.Count > 0) break;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mac, bridgePort) in macToBridgePort)
        {
            var name = portToIf.TryGetValue(bridgePort, out var ifIndex) &&
                       ifNames.TryGetValue(ifIndex, out var n) && n.Length > 0
                ? n
                : $"port {bridgePort}";
            result[mac] = name;
        }
        return result;
    }

    private static int IntOf(byte[] value)
    {
        int v = 0;
        foreach (var b in value) v = (v << 8) | b;
        return value.Length is > 0 and <= 4 ? v : 0;
    }
}
