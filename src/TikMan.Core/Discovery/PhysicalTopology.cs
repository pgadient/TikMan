namespace TikMan.Core.Discovery;

/// <summary>A node's role in the physical map – which is what its colour says. Same pastels as the GUI.</summary>
public enum TopoRole { Internet, Gateway, Infrastructure, Client }

/// <summary>One device fed to the topology builder. UI-free: an id, its address/MAC, the two label lines,
/// and whether it is infrastructure (router/switch/AP/firewall) – bridges are drawn as the spine.</summary>
public sealed record TopoInputDevice(string Id, string Ip, string Mac, string Title, string Detail,
    bool IsInfrastructure);

/// <summary>A laid-out box: position, size and CSS-ready hex colours – the same draw data the GUI's
/// nodes and the PDF export carry, so the web SVG renders it directly.</summary>
public sealed record TopoBox(string Key, string DeviceId, string Title, string Detail, string Mac,
    double X, double Y, double W, double H, string Fill, string Line, string Text);

public sealed record TopoLink(string From, string To);

public sealed record TopoLayout(IReadOnlyList<TopoBox> Nodes, IReadOnlyList<TopoLink> Edges);

/// <summary>Builds the physical topology from bridge forwarding tables, UI-free so the headless host and
/// (later) the GUI can share it. The heuristic is the GUI's, ported verbatim: a device hangs off the
/// bridge that sees its MAC on the <b>emptiest non-uplink port</b> – an uplink port sees the whole rest
/// of the network, the true edge port sees almost nothing else. Bridges chain under their own proven
/// parent; ≥3 devices on one port get grouped under a shared port node (an unseen switch); anything with
/// no forwarding-table evidence gathers honestly under an "unknown path" node rather than being invented
/// somewhere. Layout is tiered by depth, wide tiers wrap into a grid so the map never stretches for miles.
/// <para>Traceroute-based routed hops (the GUI's step 3) are omitted here – the headless host has no
/// traces yet; FDB placement plus the unknown bucket already draws the switch topology, which is the
/// part credentials/SNMP actually prove.</para></summary>
public static class PhysicalTopology
{
    private const double NodeWidth = 168, NodeHeight = 56, ColGap = 22, RowGap = 18, TierGap = 90;
    private const int MaxCols = 10, PortGroupThreshold = 3;

    public static TopoLayout Build(
        IReadOnlyList<TopoInputDevice> devices,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> fdb, // bridgeId → (normMAC → port)
        string gatewayIp,
        IReadOnlyDictionary<(string BridgeId, string Port), string>? ssids = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? traces = null)   // deviceIp → hop IPs
    {
        var boxes = new List<TopoBox>();
        var links = new List<TopoLink>();
        var placed = new Dictionary<string, TopoBox>();          // key → box
        var levels = new Dictionary<string, int>();
        var tierCount = new Dictionary<int, int>();

        TopoBox Add(string key, string? deviceId, string title, string detail, string mac, TopoRole role)
        {
            var (fill, line, text) = Palette(role);
            var box = new TopoBox(key, deviceId ?? "", title, detail, mac, 0, 0, NodeWidth, NodeHeight, fill, line, text);
            boxes.Add(box);
            placed[key] = box;
            return box;
        }
        void PlaceAt(int level, string key)
        {
            int idx = tierCount.TryGetValue(level, out var c) ? c : 0;
            tierCount[level] = idx + 1;
            double baseY = 40 + level * (NodeHeight + TierGap);
            var b = placed[key];
            placed[key] = b with { X = idx % MaxCols * (NodeWidth + ColGap),
                                   Y = baseY + idx / MaxCols * (NodeHeight + RowGap) };
            levels[key] = level;
        }
        void Connect(string from, string to) => links.Add(new TopoLink(from, to));

        var byId = devices.Where(d => d.Id.Length > 0).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var gwDev = devices.FirstOrDefault(d => d.Ip.Length > 0 &&
            string.Equals(d.Ip, gatewayIp, StringComparison.OrdinalIgnoreCase));
        var gwKey = gwDev?.Id ?? "::gw";
        Add(gwKey, gwDev?.Id, gwDev?.Title ?? (gatewayIp.Length > 0 ? gatewayIp : "Gateway"),
            gwDev?.Detail ?? gatewayIp, gwDev?.Mac ?? "", TopoRole.Gateway);
        PlaceAt(0, gwKey);
        var gwMac = gwDev is not null ? NormalizeMac(gwDev.Mac) : "";

        // Each bridge's uplink = the port on which it sees the gateway. Everything on that port lives
        // beyond it toward the root, so an uplink port never counts as where a device hangs.
        var uplink = new Dictionary<string, string>();
        foreach (var (bridgeId, table) in fdb)
            uplink[bridgeId] = gwDev is not null && bridgeId == gwDev.Id ? ""
                : gwMac.Length > 0 && table.TryGetValue(gwMac, out var up) ? up : "";

        (string BridgeId, string Port)? AttachOf(string macRaw, string? selfId)
        {
            var mac = NormalizeMac(macRaw);
            if (mac.Length == 0) return null;
            (string BridgeId, string Port)? best = null;
            int bestCount = int.MaxValue;
            foreach (var (bridgeId, table) in fdb)
            {
                if (bridgeId == selfId || !table.TryGetValue(mac, out var port)) continue;
                if ((gwDev is null || bridgeId != gwDev.Id) && port == uplink[bridgeId]) continue;
                int count = table.Values.Count(v => v == port);
                if (count < bestCount) { best = (bridgeId, port); bestCount = count; }
            }
            return best;
        }

        string PortLabel(string bridgeId, string port) =>
            port.Length > 0 && ssids is not null && ssids.TryGetValue((bridgeId, port), out var ssid)
                ? $"{port} ({ssid})" : port;

        // 1) The bridges, each under its proven parent (recursively, cycles guarded).
        var nodeKeyOf = new Dictionary<string, string>(); // deviceId → placed key
        if (gwDev is not null) nodeKeyOf[gwDev.Id] = gwKey;

        string EnsureBridge(string bridgeId, HashSet<string> path)
        {
            if (nodeKeyOf.TryGetValue(bridgeId, out var existing)) return existing;
            var dev = byId.TryGetValue(bridgeId, out var b) ? b : null;
            string parentKey = gwKey;
            string port = "";
            if (path.Add(bridgeId) && dev is not null && AttachOf(dev.Mac, bridgeId) is { } at)
            {
                parentKey = gwDev is not null && at.BridgeId == gwDev.Id ? gwKey : EnsureBridge(at.BridgeId, path);
                port = PortLabel(at.BridgeId, at.Port);
            }
            var key = bridgeId;
            Add(key, dev?.Id, dev?.Title ?? bridgeId, JoinDetail(dev?.Detail, port), dev?.Mac ?? "", TopoRole.Infrastructure);
            nodeKeyOf[bridgeId] = key;
            int level = levels[parentKey] + 1;
            PlaceAt(level, key);
            Connect(parentKey, key);
            return key;
        }
        foreach (var bridgeId in fdb.Keys.Where(id => gwDev is null || id != gwDev.Id).ToList())
            EnsureBridge(bridgeId, new HashSet<string>());

        // 2) Every other device: grouped by (bridge, port); ≥3 on one port ⇒ a shared port node.
        var attachGroups = new Dictionary<(string, string), List<TopoInputDevice>>();
        foreach (var d in devices)
        {
            if ((gwDev is not null && d.Id == gwDev.Id) || nodeKeyOf.ContainsKey(d.Id)) continue;
            if (AttachOf(d.Mac, d.Id) is { } at && nodeKeyOf.ContainsKey(at.BridgeId))
            {
                var key = (at.BridgeId, at.Port);
                if (!attachGroups.TryGetValue(key, out var list)) attachGroups[key] = list = new();
                list.Add(d);
            }
        }

        var attachedViaFdb = new HashSet<string>();
        foreach (var ((bridgeId, port), members) in attachGroups)
        {
            var bridgeKey = nodeKeyOf[bridgeId];
            string parentForLeaves = bridgeKey;
            int leafLevel = levels[bridgeKey] + 1;
            if (members.Count >= PortGroupThreshold)
            {
                var portKey = $"::port:{bridgeId}:{port}";
                Add(portKey, null, PortLabel(bridgeId, port), "", "", TopoRole.Infrastructure);
                PlaceAt(leafLevel, portKey);
                Connect(bridgeKey, portKey);
                parentForLeaves = portKey;
                leafLevel += 1;
            }
            foreach (var d in members.OrderBy(x => Ipv4SortKey(x.Ip)))
            {
                var label = members.Count >= PortGroupThreshold ? "" : PortLabel(bridgeId, port);
                Add(d.Id, d.Id, d.Title, JoinDetail(d.Detail, label), d.Mac,
                    d.IsInfrastructure ? TopoRole.Infrastructure : TopoRole.Client);
                nodeKeyOf[d.Id] = d.Id;
                PlaceAt(leafLevel, d.Id);
                Connect(parentForLeaves, d.Id);
                attachedViaFdb.Add(d.Id);
            }
        }

        // 3) Whatever the FDB couldn't place: a traced router path (routed segments), else "unknown".
        var byIp = devices.Where(d => d.Ip.Length > 0)
            .GroupBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First());
        var hopNodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // hopIp → placed key
        string? unknownKey = null;
        foreach (var d in devices.OrderBy(x => Ipv4SortKey(x.Ip)))
        {
            if ((gwDev is not null && d.Id == gwDev.Id) || nodeKeyOf.ContainsKey(d.Id) || attachedViaFdb.Contains(d.Id))
                continue;

            // A traced route: chain its routers (each hop != the gateway) under the root, then the leaf.
            if (traces is not null && traces.TryGetValue(d.Ip, out var hops) && hops.Count > 0)
            {
                var parentKey = gwKey;
                int level = 0;
                foreach (var hop in hops.Where(h => !string.Equals(h, gatewayIp, StringComparison.OrdinalIgnoreCase)))
                {
                    level++;
                    if (!hopNodes.TryGetValue(hop, out var hopKey))
                    {
                        // Reuse a known device as the hop when we have one and it isn't placed yet.
                        if (byIp.TryGetValue(hop, out var hopDev) && !nodeKeyOf.ContainsKey(hopDev.Id))
                        {
                            hopKey = hopDev.Id;
                            Add(hopKey, hopDev.Id, hopDev.Title, hopDev.Detail, hopDev.Mac, TopoRole.Infrastructure);
                            nodeKeyOf[hopDev.Id] = hopKey;
                        }
                        else { hopKey = "::hop:" + hop; Add(hopKey, null, hop, hop, "", TopoRole.Infrastructure); }
                        hopNodes[hop] = hopKey;
                        PlaceAt(level, hopKey);
                        Connect(parentKey, hopKey);
                    }
                    parentKey = hopKey;
                }
                Add(d.Id, d.Id, d.Title, d.Detail, d.Mac, d.IsInfrastructure ? TopoRole.Infrastructure : TopoRole.Client);
                nodeKeyOf[d.Id] = d.Id;
                PlaceAt(levels.TryGetValue(parentKey, out var pl) ? pl + 1 : 1, d.Id);
                Connect(parentKey, d.Id);
                continue;
            }

            if (unknownKey is null)
            {
                unknownKey = "::unknown";
                Add(unknownKey, null, "Path unknown", "", "", TopoRole.Internet);
                PlaceAt(1, unknownKey);
                Connect(gwKey, unknownKey);
            }
            Add(d.Id, d.Id, d.Title, d.Detail, d.Mac,
                d.IsInfrastructure ? TopoRole.Infrastructure : TopoRole.Client);
            nodeKeyOf[d.Id] = d.Id;
            PlaceAt(2, d.Id);
            Connect(unknownKey, d.Id);
        }

        return new TopoLayout(boxes.Select(b => placed[b.Key]).ToList(), links);
    }

    private static string JoinDetail(string? detail, string port) =>
        port.Length > 0 ? (string.IsNullOrEmpty(detail) ? port : $"{detail} · {port}") : (detail ?? "");

    /// <summary>Twelve bare uppercase hex digits, whatever the separators; "" when it isn't a MAC.</summary>
    public static string NormalizeMac(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? hex : "";
    }

    private static uint Ipv4SortKey(string ip)
    {
        var parts = (ip ?? "").Split('.');
        if (parts.Length != 4) return uint.MaxValue;
        uint v = 0;
        foreach (var p in parts) { if (!byte.TryParse(p, out var b)) return uint.MaxValue; v = v << 8 | b; }
        return v;
    }

    private static (string Fill, string Line, string Text) Palette(TopoRole role) => role switch
    {
        TopoRole.Gateway => ("#FFF1DF", "#F3C48A", "#B26B12"),
        TopoRole.Infrastructure => ("#EAF4FB", "#A9CFE7", "#2C6C93"),
        TopoRole.Client => ("#EDF8ED", "#AFD8AF", "#3F7A46"),
        _ => ("#EEF1F3", "#B8C4CB", "#546E7A"),
    };
}
