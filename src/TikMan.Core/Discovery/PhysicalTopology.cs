namespace TikMan.Core.Discovery;

/// <summary>A node's role in the physical map – which is what its colour says. Same pastels as the GUI.</summary>
public enum TopoRole { Internet, Gateway, Infrastructure, Client, Segment }

/// <summary>One device fed to the topology builder. UI-free: an id, its address/MAC, the two label lines,
/// and whether it is infrastructure (router/switch/AP/firewall) – bridges are drawn as the spine.</summary>
/// <param name="Vendor">The manufacturer on its own line ("MikroTik").</param>
/// <param name="Model">The model on its own line ("CCR2004-16G-2S+"), with the manufacturer stripped off
/// the front when it repeats it.</param>
/// <param name="Kind">The device category label, in the user's language.</param>
public sealed record TopoInputDevice(string Id, string Ip, string Mac, string Title, string Detail,
    bool IsInfrastructure, string Vendor = "", string Model = "", string Kind = "");

/// <summary>A laid-out box: position, size and CSS-ready hex colours – the same draw data the GUI's
/// nodes and the PDF export carry, so the web SVG renders it directly.</summary>
/// <param name="Vendor">Manufacturer, on its OWN line. ⚠️ Vendor and model used to share one line, and on
/// real hardware that line is routinely too long for the box – "Hewlett Packard Enterprise ProLiant DL380
/// Gen10" – so every renderer ellipsised it and the model, the more specific half, was what got cut. Two
/// short lines fit where one long one never did.</param>
/// <param name="Model">Model, on its own line, with the manufacturer stripped off the front when the model
/// string repeats it.</param>
/// <param name="Kind">The device category ("Router", "Switch", …) – what the box IS, which is what makes
/// the shape of a network readable at a glance rather than after reading every name.</param>
public sealed record TopoBox(string Key, string DeviceId, string Title, string Detail, string Mac,
    double X, double Y, double W, double H, string Fill, string Line, string Text,
    string Vendor = "", string Model = "", string Kind = "");

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
    // ⚠️ Taller than the two lines it used to hold: a box now carries name, vendor, model, category and
    // address. The height is shared by every renderer (canvas, PNG, PDF, GraphML, draw.io, the web SVG),
    // so it has to leave room for all five – a box sized for fewer silently clips the rest.
    private const double NodeWidth = 178, NodeHeight = 100, ColGap = 22, RowGap = 18, TierGap = 90;
    private const int MaxCols = 10, PortGroupThreshold = 3;

    /// <summary>The address-distribution view: Internet → network → equal address blocks → devices. No
    /// forwarding tables needed, so it is instant and works on any network.
    ///
    /// <para>⚠️ This used to be a flat fan of every device off one "Internet" node, which showed the
    /// inventory but said nothing about the addressing – on a /21 with a hundred hosts it was a wall of
    /// boxes. Grouping by equal blocks (see <see cref="IpSegments"/>) makes the shape of the network
    /// visible: which parts are dense, which are empty, and where the boundaries fall.</para>
    ///
    /// <para>Empty blocks are drawn too, on purpose. "Nothing lives in the upper half of this range" is a
    /// fact about the network, and a view that silently omits it looks like a smaller network.</para></summary>
    /// <param name="localCidrs">The networks this host is attached to, as CIDRs. Their real prefix decides
    /// how the addresses are divided; a device outside all of them is grouped by its own /24.</param>
    public static TopoLayout BuildLogical(IReadOnlyList<TopoInputDevice> devices,
        IReadOnlyList<string>? localCidrs = null)
    {
        var placed = new Dictionary<string, TopoBox>();
        var links = new List<TopoLink>();
        var order = new List<string>();   // insertion order, so the result is stable

        TopoBox Box(string key, string deviceId, string title, string detail, string mac, TopoRole role,
            string vendor = "", string model = "", string kind = "")
        {
            var (f, l, t) = Palette(role);
            var box = new TopoBox(key, deviceId, title, detail, mac, 0, 0, NodeWidth, NodeHeight, f, l, t,
                vendor, model, kind);
            placed[key] = box;
            order.Add(key);
            return box;
        }

        Box("internet", "", "Internet", "", "", TopoRole.Internet);

        // Every network gets its blocks up front, so a device only has to find the block it falls into.
        // Keyed by first address: two adapters on overlapping ranges must not produce two sets of blocks.
        var segments = new List<(uint First, uint Last, string Key)>();
        var netOfSegment = new Dictionary<string, string>();
        var deviceCount = new Dictionary<string, int>();

        /// <param name="split">Whether to divide it into blocks. ⚠️ Only true for a network whose real
        /// prefix is known from an adapter. The /24 invented for a stray address is a guess already, and
        /// carving a guess into four sub-blocks would present invented structure as measured structure –
        /// so those devices hang straight off their /24 group.
        void AddNetwork(string cidr, bool split)
        {
            if (!IpSegments.TryParseCidr(cidr, out var net, out var prefix)) return;
            var netKey = "net:" + IpSegments.ToIp(net) + "/" + prefix;
            if (placed.ContainsKey(netKey)) return;

            Box(netKey, "", $"{IpSegments.ToIp(net)}/{prefix}", "", "", TopoRole.Internet);
            links.Add(new TopoLink("internet", netKey));

            if (!split)
            {
                long size = 1L << (32 - prefix);
                segments.Add((net, (uint)(net + size - 1), netKey));
                deviceCount[netKey] = 0;
                return;
            }

            foreach (var s in IpSegments.Plan(cidr))
            {
                var segKey = "seg:" + s.Cidr;
                if (placed.ContainsKey(segKey)) continue;
                // The range on the second line: the prefix says the same thing, but only to someone who
                // does the arithmetic.
                Box(segKey, "", s.Cidr, s.Range, "", TopoRole.Segment);
                links.Add(new TopoLink(netKey, segKey));
                segments.Add((s.First, s.Last, segKey));
                netOfSegment[segKey] = netKey;
                deviceCount[segKey] = 0;
            }
        }

        foreach (var cidr in localCidrs ?? Array.Empty<string>()) AddNetwork(cidr, split: true);

        // ⚠️ Devices outside every local network still need a home, and inventing one /24 per stray address
        // keeps them grouped the way the rest of the view is grouped instead of dumping them in one bag.
        foreach (var d in devices)
        {
            if (!IpSegments.TryParseIp(d.Ip, out var v)) continue;
            if (segments.Any(s => v >= s.First && v <= s.Last)) continue;
            var fallback = IpSegments.EnclosingSlash24(d.Ip);
            if (fallback.Length > 0) AddNetwork(fallback, split: false);
        }

        int i = 0;
        foreach (var d in devices)
        {
            var key = "d" + i++;
            Box(key, d.Id, d.Title, d.Ip, d.Mac,
                d.IsInfrastructure ? TopoRole.Infrastructure : TopoRole.Client, d.Vendor, d.Model, d.Kind);

            var parent = "internet";
            if (IpSegments.TryParseIp(d.Ip, out var v))
                foreach (var s in segments)
                    if (v >= s.First && v <= s.Last) { parent = s.Key; deviceCount[s.Key]++; break; }
            links.Add(new TopoLink(parent, key));
        }

        // A block says how many devices are in it – the number is the distribution, and reading it off the
        // boxes underneath only works while they all fit on screen.
        foreach (var (key, count) in deviceCount)
            placed[key] = placed[key] with { Detail = placed[key].Detail + "  ·  " + count };

        Arrange(placed, links);
        return new TopoLayout(order.Select(k => placed[k]).ToList(), links);
    }

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

        TopoBox Add(string key, string? deviceId, string title, string detail, string mac, TopoRole role,
            string vendor = "", string model = "", string kind = "")
        {
            var (fill, line, text) = Palette(role);
            var box = new TopoBox(key, deviceId ?? "", title, detail, mac, 0, 0, NodeWidth, NodeHeight, fill, line, text,
                vendor, model, kind);
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
            gwDev?.Detail ?? gatewayIp, gwDev?.Mac ?? "", TopoRole.Gateway,
            gwDev?.Vendor ?? "", gwDev?.Model ?? "", gwDev?.Kind ?? "");
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
            Add(key, dev?.Id, dev?.Title ?? bridgeId, JoinDetail(dev?.Detail, port), dev?.Mac ?? "", TopoRole.Infrastructure, dev?.Vendor ?? "", dev?.Model ?? "", dev?.Kind ?? "");
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
                // ⚠️ Hardware and Kind travel with EVERY device node, not just the infrastructure ones.
                // These two arguments were missing here and in the "path unknown" branch below – the two
                // paths every ordinary client takes – so the map named vendor and model on the switches and
                // left the clients as a bare hostname, even where the facts were sitting right there in the
                // inventory. It showed up first in the exports, which is where a map gets read carefully.
                Add(d.Id, d.Id, d.Title, JoinDetail(d.Detail, label), d.Mac,
                    d.IsInfrastructure ? TopoRole.Infrastructure : TopoRole.Client, d.Vendor, d.Model, d.Kind);
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
                            Add(hopKey, hopDev.Id, hopDev.Title, hopDev.Detail, hopDev.Mac, TopoRole.Infrastructure, hopDev.Vendor, hopDev.Model, hopDev.Kind);
                            nodeKeyOf[hopDev.Id] = hopKey;
                        }
                        else { hopKey = "::hop:" + hop; Add(hopKey, null, hop, hop, "", TopoRole.Infrastructure); }
                        hopNodes[hop] = hopKey;
                        PlaceAt(level, hopKey);
                        Connect(parentKey, hopKey);
                    }
                    parentKey = hopKey;
                }
                Add(d.Id, d.Id, d.Title, d.Detail, d.Mac, d.IsInfrastructure ? TopoRole.Infrastructure : TopoRole.Client, d.Vendor, d.Model, d.Kind);
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
                d.IsInfrastructure ? TopoRole.Infrastructure : TopoRole.Client, d.Vendor, d.Model, d.Kind);
            nodeKeyOf[d.Id] = d.Id;
            PlaceAt(2, d.Id);
            Connect(unknownKey, d.Id);
        }

        // ⚠️ Everything above assigned positions the moment a node was discovered, filling each tier from
        // the left in whatever order the forwarding tables happened to yield. That is why the map looked
        // tangled: a switch could sit at the far left of tier 1 with its clients scattered across tier 2,
        // and the edges crossed the whole picture. Positions are therefore thrown away here and laid out
        // properly, once the tree is actually known.
        Arrange(placed, links);
        return new TopoLayout(boxes.Select(b => placed[b.Key]).ToList(), links);
    }

    /// <summary>Lays the finished tree out tidily: the gateway on top, infrastructure below it one tier per
    /// hop, and each node's client leaves packed into a compact block underneath it. Every parent ends up
    /// centred over everything that hangs from it, so an edge is a short vertical line instead of a
    /// diagonal across the map.
    ///
    /// <para>Two kinds of child are treated differently on purpose. A <b>branch</b> (a switch with its own
    /// clients) needs the full width of its subtree and gets its own column. A <b>leaf</b> (a plain client)
    /// needs nothing below it, so leaves are stacked into a grid a few columns wide – otherwise a switch
    /// with forty clients produces a row forty nodes long and the map is unreadable at any zoom.</para></summary>
    private static void Arrange(Dictionary<string, TopoBox> placed, List<TopoLink> links)
    {
        var children = new Dictionary<string, List<string>>();
        var hasParent = new HashSet<string>();
        foreach (var l in links)
        {
            if (!placed.ContainsKey(l.From) || !placed.ContainsKey(l.To)) continue;
            if (!children.TryGetValue(l.From, out var list)) children[l.From] = list = new List<string>();
            // A node reached twice would otherwise be laid out twice and the second pass would win.
            if (hasParent.Add(l.To)) list.Add(l.To);
        }

        // Stable order: infrastructure first (it carries subtrees), then by title, so two runs over the same
        // network produce the same picture instead of shuffling with dictionary order.
        foreach (var list in children.Values)
            list.Sort((a, b) =>
            {
                var ab = children.ContainsKey(a); var bb = children.ContainsKey(b);
                if (ab != bb) return ab ? -1 : 1;
                return string.Compare(placed[a].Title, placed[b].Title, StringComparison.OrdinalIgnoreCase);
            });

        const int LeafCols = 6;                 // leaves per row inside one parent's block
        double colStep = NodeWidth + ColGap;

        // ⚠️ Cycle guard. The parent/child map is built from measured forwarding tables, and two bridges
        // that each list the other's MAC on a downlink produce A→B→A. Without this the recursion below
        // never terminates – and a StackOverflowException cannot be caught, so it takes the whole process
        // down rather than showing a bad map. A node already laid out is simply not descended into again.
        var done = new HashSet<string>();

        // Returns the width this subtree occupies, in columns, and positions it starting at column `col`.
        double Layout(string key, int depth, double col)
        {
            if (!done.Add(key)) return 0;
            var kids = children.TryGetValue(key, out var k) ? k : new List<string>();
            var leaves = kids.Where(c => !children.ContainsKey(c)).ToList();
            var branches = kids.Where(children.ContainsKey).ToList();

            double used = 0;
            // Branches first, each taking as much width as it needs.
            foreach (var b in branches)
                used += Layout(b, depth + 1, col + used);

            // Leaves fill a block under the parent, LeafCols wide.
            if (leaves.Count > 0)
            {
                var blockCols = Math.Min(LeafCols, leaves.Count);
                for (var i = 0; i < leaves.Count; i++)
                {
                    // ⚠️ Leaves are positioned here rather than through Layout, so they must be marked done
                    // explicitly. Without this the "anything not reached" pass below counted every leaf as
                    // an unplaced root and laid the whole map out again as one endless horizontal strip.
                    done.Add(leaves[i]);
                    var lx = col + used + i % blockCols;
                    var ly = depth + 1 + i / blockCols;
                    placed[leaves[i]] = placed[leaves[i]] with
                    {
                        X = 40 + lx * colStep,
                        Y = 40 + ly * (NodeHeight + TierGap) - (ly - depth - 1) * (TierGap - RowGap),
                    };
                }
                used += blockCols;
            }

            var width = Math.Max(1, used);
            // The parent sits centred over everything it spans.
            placed[key] = placed[key] with
            {
                X = 40 + (col + (width - 1) / 2.0) * colStep,
                Y = 40 + depth * (NodeHeight + TierGap),
            };
            return width;
        }

        var roots = placed.Keys.Where(k => !hasParent.Contains(k)).ToList();
        double next = 0;
        foreach (var r in roots) next += Layout(r, 0, next) + 1;   // a column of air between roots

        // ⚠️ A cycle among the top nodes leaves EVERY node with a parent, so the list above comes back
        // empty and nothing is positioned at all – the map would keep whatever the discovery order happened
        // to assign. Anything the pass did not reach is laid out as its own root here, so a strange
        // forwarding table costs tidiness, never the picture.
        foreach (var k in placed.Keys.Where(k => !done.Contains(k)).ToList())
            next += Layout(k, 0, next) + 1;
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
        // Address blocks are structure, not equipment – a cool neutral so they read as the frame the
        // devices sit in rather than as another class of device.
        TopoRole.Segment => ("#F2F0F8", "#C3BCD8", "#5B4E86"),
        TopoRole.Infrastructure => ("#EAF4FB", "#A9CFE7", "#2C6C93"),
        TopoRole.Client => ("#EDF8ED", "#AFD8AF", "#3F7A46"),
        _ => ("#EEF1F3", "#B8C4CB", "#546E7A"),
    };
}
