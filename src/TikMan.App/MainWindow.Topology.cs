using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TikMan.Core.Discovery;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>The two topology tabs.
///
/// The *logical* view draws the addressing: internet on top, one blue node per address range (a pure
/// range – deliberately no mask, since the visual /24 grouping says nothing about the real subnet
/// size), and every device of that range in green below it.
///
/// The *physical* view draws what a scan can actually prove about the wiring: the gateway on top and
/// a traceroute-derived path to every device, so a device behind another router visibly hangs behind
/// it. It cannot see switches – nothing short of reading every switch's bridge table could – so
/// same-segment devices hang directly off the gateway. The traces run when the tab is first opened.</summary>
public partial class MainWindow
{
    private const double NodeWidth = 168, NodeHeight = 56, ColGap = 22, RowGap = 18;

    private readonly Dictionary<string, Point> _topoPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TopoNode> _topoNodes = new();
    private readonly List<TopoEdge> _topoEdges = new();

    private bool _topoPhysical;                       // which of the two views the canvas is showing
    private Dictionary<string, List<string>>? _traceResults;   // device ip → router hops (physical view)
    private Dictionary<DeviceViewModel, Dictionary<string, string>>? _fdb; // bridge → (MAC → port)
    private bool _tracing;

    private TopoNode? _dragNode;
    private Point _dragOffset;
    private Point _panStart;
    private bool _panning;

    private sealed class TopoNode
    {
        public required string Key { get; init; }      // MAC/IP, or a synthetic key for pseudo-nodes
        public required Border Visual { get; init; }
        public DeviceViewModel? Device { get; init; }  // null for "Internet" / an address range
        public Point Position { get; set; }
    }

    private sealed class TopoEdge
    {
        public required Line Visual { get; init; }
        public required TopoNode From { get; init; }
        public required TopoNode To { get; init; }
    }

    // ---- entry points ---------------------------------------------------------------------------

    /// <summary>Shows one of the topology views: fills the whole window (the details pane folds away),
    /// builds the graph, and – for the physical view – starts the traceroute sweep on first open.</summary>
    private async void ShowTopology(bool physical)
    {
        _topoPhysical = physical;
        TopologyHost.Visibility = Visibility.Visible;
        DeviceGrid.Visibility = Visibility.Collapsed;
        SetTopologyFullscreen(true);

        if (physical && _traceResults is null && !_tracing)
            await RunTracesAsync();

        BuildTopology();
        // Fit only after the layout pass: the host was collapsed a moment ago, so its ActualWidth is
        // still 0 right now – fitting against that pins the graph into the top-left corner.
        await Dispatcher.InvokeAsync(() => Topology_Fit_Click(this, new RoutedEventArgs()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void HideTopology()
    {
        TopologyHost.Visibility = Visibility.Collapsed;
        DeviceGrid.Visibility = Visibility.Visible;
        SetTopologyFullscreen(false);
    }

    /// <summary>A map wants the whole window: the details pane and its splitter fold away while a
    /// topology tab is active and come back when the user returns to the lists.</summary>
    private void SetTopologyFullscreen(bool on)
    {
        if (on)
        {
            DetailRow.MinHeight = 0;
            DetailRow.Height = new GridLength(0);
            SplitterRow.Height = new GridLength(0);
        }
        else
        {
            DetailRow.MinHeight = 120;
            DetailRow.Height = new GridLength(300);
            SplitterRow.Height = new GridLength(6);
        }
    }

    /// <summary>Collects the physical evidence, in parallel: a traceroute to every device (the L3
    /// paths), and the bridge forwarding tables of every RouterOS device with credentials – the FDB is
    /// what proves which switch port a device hangs off, since switching is invisible to traceroute.
    /// Re-run via "Re-arrange" after a rescan.</summary>
    private async Task RunTracesAsync()
    {
        _tracing = true;
        SetStatus(T("Topo_Tracing"));
        try
        {
            var targets = _devices.Where(d => d.HasIpv4).Select(d => d.Ipv4Address).Distinct().ToList();
            var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var tracesTask = Task.WhenAll(targets.Select(async ip =>
            {
                var hops = await TraceRoute.TraceAsync(ip);
                if (hops is not null) lock (results) results[ip] = hops;
            }));

            var bridges = _devices.Where(d => d.HasIpv4 &&
                (d.Board.Length > 0 || d.IdentifiedVendor == "MikroTik")).ToList();
            var fdb = new Dictionary<DeviceViewModel, Dictionary<string, string>>();
            var fdbTask = Task.WhenAll(bridges.Select(async d =>
            {
                var table = await d.GetBridgeHostsAsync();
                if (table is not null) lock (fdb) fdb[d] = table;
            }));

            await Task.WhenAll(tracesTask, fdbTask);
            _traceResults = results;
            _fdb = fdb;
            SetStatus(T("Topo_TraceDone", results.Count));
        }
        finally { _tracing = false; }
    }

    // ---- building -------------------------------------------------------------------------------

    private void BuildTopology(bool resetPositions = false)
    {
        if (resetPositions)
        {
            _topoPositions.Clear();
            if (_topoPhysical) _traceResults = null; // re-arrange on the physical tab = trace again
        }
        TopologyCanvas.Children.Clear();
        _topoNodes.Clear();
        _topoEdges.Clear();

        var devices = _devices.Where(d => d.HasIpv4).ToList();
        if (devices.Count == 0) return;

        if (_topoPhysical) BuildPhysical(devices);
        else BuildLogical(devices);
        RedrawEdges();
    }

    /// <summary>Internet → one blue node per address range → that range's devices in green. The range
    /// nodes are ranges and nothing more: no mask (the /24 grouping is visual, the real subnet may be
    /// a /21), and no device doubles as a range.</summary>
    private void BuildLogical(List<DeviceViewModel> devices)
    {
        var internet = AddNode("::internet", null, T("Topo_Internet"), "", "", Role.Internet);

        var ranges = devices.GroupBy(d => RangeOf(d.Ipv4Address))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();

        double x = 0;
        const double rangeRow = 130, deviceRow = 250;

        foreach (var range in ranges)
        {
            var members = range.OrderBy(d => d.Ipv4SortKey).ToList();
            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(members.Count)));
            double width = cols * (NodeWidth + ColGap);

            var rangeNode = AddNode("::range:" + range.Key, null, range.Key + ".x", "", "", Role.Infrastructure);
            Place(rangeNode, x + width / 2 - NodeWidth / 2, rangeRow);
            Connect(internet, rangeNode);

            for (int i = 0; i < members.Count; i++)
            {
                var n = AddDeviceNode(members[i], Role.Client);
                Place(n, x + i % cols * (NodeWidth + ColGap),
                         deviceRow + i / cols * (NodeHeight + RowGap));
                Connect(rangeNode, n);
            }

            x += width + 70;
        }

        Place(internet, Math.Max(0, x / 2 - NodeWidth / 2), 20);
    }

    /// <summary>Gateway on top, then the wiring as far as it can be *proven*: the bridge forwarding
    /// tables of the RouterOS switches/APs say which port every MAC hangs off (switching is invisible
    /// to traceroute, the FDB is the only witness), and traceroute contributes the routed hops. A
    /// device attaches to the bridge that sees its MAC on the emptiest non-uplink port – an uplink
    /// port "sees" the whole rest of the network, the true edge port sees almost nothing else. Devices
    /// with no evidence at all gather under an "unknown path" node rather than being placed somewhere
    /// invented.</summary>
    private void BuildPhysical(List<DeviceViewModel> devices)
    {
        var byIp = devices.ToDictionary(d => d.Ipv4Address, d => d, StringComparer.OrdinalIgnoreCase);
        var traces = _traceResults ?? new Dictionary<string, List<string>>();
        var fdb = _fdb ?? new Dictionary<DeviceViewModel, Dictionary<string, string>>();

        var gwIp = TraceRoute.DefaultGateway();
        byIp.TryGetValue(gwIp, out var gwDev);
        var root = gwDev is not null
            ? AddDeviceNode(gwDev, Role.Gateway)
            : AddNode("::gw", null, gwIp.Length > 0 ? gwIp : T("Topo_Gateway"), gwIp, "", Role.Gateway);
        var gwMac = gwDev is not null ? DeviceViewModel.NormalizeMac(gwDev.Model.MacAddress) : "";

        // Each bridge's uplink: the port on which it sees the gateway. Everything on that port lives
        // *beyond* it – towards the root – so an uplink port never counts as where a device hangs.
        var uplink = new Dictionary<DeviceViewModel, string>();
        foreach (var (sw, table) in fdb)
            uplink[sw] = sw == gwDev ? ""
                : gwMac.Length > 0 && table.TryGetValue(gwMac, out var up) ? up : "";

        // Where does a MAC hang? The bridge that sees it on the *emptiest* non-uplink port wins: the
        // switch one hop away sees it on a port with half the network behind it, the edge switch sees
        // it on a port with (almost) only that device.
        (DeviceViewModel Bridge, string Port)? AttachOf(string macRaw, DeviceViewModel? self)
        {
            var mac = DeviceViewModel.NormalizeMac(macRaw);
            if (mac.Length == 0) return null;
            (DeviceViewModel Bridge, string Port)? best = null;
            int bestCount = int.MaxValue;
            foreach (var (sw, table) in fdb)
            {
                if (sw == self || !table.TryGetValue(mac, out var port)) continue;
                if (sw != gwDev && port == uplink[sw]) continue;   // points at the root, not at the MAC
                int count = table.Values.Count(v => v == port);
                if (count < bestCount) { best = (sw, port); bestCount = count; }
            }
            return best;
        }

        // Layout plumbing: one lane per depth level.
        var nextX = new Dictionary<int, double>();
        void PlaceAt(int level, TopoNode n)
        {
            double x = nextX.TryGetValue(level, out var v) ? v : 0;
            Place(n, x, 40 + level * (NodeHeight + 70));
            nextX[level] = x + NodeWidth + ColGap;
        }
        var levels = new Dictionary<TopoNode, int> { [root] = 0 };
        PlaceAt(0, root);

        var nodes = new Dictionary<DeviceViewModel, TopoNode> ();
        if (gwDev is not null) nodes[gwDev] = root;

        // 1) The bridges themselves, each under its proven parent (recursively, cycles guarded).
        TopoNode EnsureBridge(DeviceViewModel sw, HashSet<DeviceViewModel> path)
        {
            if (nodes.TryGetValue(sw, out var existing)) return existing;
            TopoNode parent = root;
            string port = "";
            if (path.Add(sw) && AttachOf(sw.Model.MacAddress, sw) is { } at)
            {
                parent = at.Bridge == sw ? root : EnsureBridge(at.Bridge, path);
                port = at.Port;
            }
            var node = AddDeviceNode(sw, Role.Infrastructure, port);
            nodes[sw] = node;
            int level = levels[parent] + 1;
            levels[node] = level;
            PlaceAt(level, node);
            Connect(parent, node);
            return node;
        }
        foreach (var sw in fdb.Keys.Where(s => s != gwDev)) EnsureBridge(sw, new HashSet<DeviceViewModel>());

        // 2) Every other device: FDB proof first, then the traced router path, then "unknown".
        TopoNode? unknown = null;
        var hopNodes = new Dictionary<string, TopoNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices.OrderBy(d => d.Ipv4SortKey))
        {
            if (d == gwDev || nodes.ContainsKey(d)) continue;

            if (AttachOf(d.Model.MacAddress, d) is { } at && nodes.TryGetValue(at.Bridge, out var bridgeNode))
            {
                var leaf = AddDeviceNode(d, Role.Client, at.Port);
                nodes[d] = leaf;
                levels[leaf] = levels[bridgeNode] + 1;
                PlaceAt(levels[leaf], leaf);
                Connect(bridgeNode, leaf);
                continue;
            }

            if (traces.TryGetValue(d.Ipv4Address, out var hops))
            {
                // Routed segments: chain the traced routers under the root.
                var parent = root;
                int level = 0;
                foreach (var hop in hops.Where(h => h != gwIp))
                {
                    level++;
                    if (!hopNodes.TryGetValue(hop, out var hopNode))
                    {
                        hopNode = byIp.TryGetValue(hop, out var hopDev) && !nodes.ContainsKey(hopDev)
                            ? AddDeviceNode(hopDev, Role.Infrastructure)
                            : AddNode("::hop:" + hop, null, hop, hop, "", Role.Infrastructure);
                        hopNodes[hop] = hopNode;
                        levels[hopNode] = level;
                        PlaceAt(level, hopNode);
                        Connect(parent, hopNode);
                    }
                    parent = hopNode;
                }
                var leaf = AddDeviceNode(d, Role.Client);
                nodes[d] = leaf;
                PlaceAt(levels.TryGetValue(parent, out var pl) ? pl + 1 : 1, leaf);
                Connect(parent, leaf);
                continue;
            }

            // No FDB sighting, no traced path: grouped honestly instead of placed somewhere invented.
            if (unknown is null)
            {
                unknown = AddNode("::unknown", null, T("Topo_NoPath"), "", "", Role.Internet);
                levels[unknown] = 1;
                PlaceAt(1, unknown);
                Connect(root, unknown);
            }
            var orphan = AddDeviceNode(d, Role.Client);
            PlaceAt(2, orphan);
            Connect(unknown, orphan);
        }
    }

    /// <summary>The first three octets – an address *range* for visual grouping, deliberately not
    /// called a subnet (the real one may be a /21; we can't know its size from here).</summary>
    private static string RangeOf(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts[..3]) : ip;
    }

    // ---- nodes ----------------------------------------------------------------------------------

    /// <summary>A node's role in the map, which is what its colour says. Pastels throughout: the map is
    /// mostly clients, so it has to stay calm enough to read, with the gateway the one warm thing on it.</summary>
    private enum Role { Internet, Gateway, Infrastructure, Client }

    private static (string Fill, string Line, string Text) Palette(Role role) => role switch
    {
        // The gateway: a soft, much-lightened orange – warm enough to find at a glance without shouting.
        Role.Gateway => ("#FFF1DF", "#F3C48A", "#B26B12"),
        // Address ranges, routers on a path, switches, APs: ice blue.
        Role.Infrastructure => ("#EAF4FB", "#A9CFE7", "#2C6C93"),
        // Everything the network exists for: a pale green.
        Role.Client => ("#EDF8ED", "#AFD8AF", "#3F7A46"),
        // The internet / placeholders: neutral, not part of the site.
        _ => ("#EEF1F3", "#B8C4CB", "#546E7A"),
    };

    private TopoNode AddDeviceNode(DeviceViewModel d, Role role, string port = "")
    {
        var key = d.Model.MacAddress.Length > 0 ? d.Model.MacAddress : d.Ipv4Address;
        var title = d.Name.Length > 0 ? d.Name : d.Ipv4Address;
        // The port is the physical fact this view exists for – "ether5" says which cable it is.
        var kind = port.Length > 0
            ? (d.DeviceType.Length > 0 ? $"{d.DeviceType} · {port}" : port)
            : d.DeviceType;
        return AddNode(key, d, title, d.Ipv4Address, d.Model.MacAddress, role, kind);
    }

    private TopoNode AddNode(string key, DeviceViewModel? device, string title, string ip, string mac,
        Role role, string kind = "")
    {
        var (fill, line, text) = Palette(role);
        SolidColorBrush Brush(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            b.Freeze();
            return b;
        }

        var stack = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Brush(text),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detail = string.Join("  ·  ", new[] { ip, kind }.Where(s => s.Length > 0));
        if (detail.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 10, Foreground = Brush(text), Opacity = 0.85,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        if (mac.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = mac, FontSize = 9, Foreground = Brush(text), Opacity = 0.55,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

        var border = new Border
        {
            Width = NodeWidth,
            MinHeight = NodeHeight,
            Background = Brush(fill),
            BorderBrush = Brush(line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = device is null ? title : $"{title}\n{ip}\n{mac}\n{device.DeviceType}",
        };

        var node = new TopoNode { Key = key, Visual = border, Device = device };
        border.Tag = node;
        border.MouseLeftButtonDown += Topology_NodeDown;

        TopologyCanvas.Children.Add(border);
        _topoNodes.Add(node);
        return node;
    }

    private void Place(TopoNode node, double x, double y)
    {
        // A node the user has already dragged stays where they put it, even across a rescan. The two
        // views keep separate memories – the same device sits elsewhere on each map.
        var key = (_topoPhysical ? "P:" : "L:") + node.Key;
        var p = _topoPositions.TryGetValue(key, out var saved) ? saved : new Point(x, y);
        node.Position = p;
        _topoPositions[key] = p;
        Canvas.SetLeft(node.Visual, p.X);
        Canvas.SetTop(node.Visual, p.Y);
        Panel.SetZIndex(node.Visual, 2);
    }

    private void Connect(TopoNode from, TopoNode to)
    {
        var line = new Line
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xC4, 0xCC, 0xD2)),
            StrokeThickness = 1.4,
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(line, 1);
        TopologyCanvas.Children.Add(line);
        _topoEdges.Add(new TopoEdge { Visual = line, From = from, To = to });
    }

    private void RedrawEdges()
    {
        foreach (var e in _topoEdges)
        {
            var a = Centre(e.From);
            var b = Centre(e.To);
            e.Visual.X1 = a.X; e.Visual.Y1 = a.Y;
            e.Visual.X2 = b.X; e.Visual.Y2 = b.Y;
        }
    }

    private static Point Centre(TopoNode n) =>
        new(n.Position.X + NodeWidth / 2, n.Position.Y + n.Visual.ActualHeight / 2);

    // ---- interaction ----------------------------------------------------------------------------

    // The mouse is captured on TopologyHost (the Border), never on the Canvas: the Canvas carries the
    // pan/zoom transform, so its own hit area travels with it and it stops seeing the very events that
    // move it. The Border stays put, which is why panning and the wheel work anywhere on the map.

    private void Topology_NodeDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: TopoNode node }) return;
        _dragNode = node;
        var p = e.GetPosition(TopologyCanvas);
        _dragOffset = new Point(p.X - node.Position.X, p.Y - node.Position.Y);
        TopologyHost.CaptureMouse();

        if (node.Device is { } vm) DeviceGrid.SelectedItem = vm; // clicking a node selects it in the list
        e.Handled = true;
    }

    private void Topology_BackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode is not null) return;         // a node handled this already
        _panning = true;
        _panStart = e.GetPosition(TopologyHost);
        TopologyHost.CaptureMouse();
        TopologyHost.Cursor = Cursors.ScrollAll;
    }

    private void Topology_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is { } node)
        {
            var p = e.GetPosition(TopologyCanvas);
            var pos = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
            node.Position = pos;
            _topoPositions[(_topoPhysical ? "P:" : "L:") + node.Key] = pos;
            Canvas.SetLeft(node.Visual, pos.X);
            Canvas.SetTop(node.Visual, pos.Y);
            RedrawEdges();
        }
        else if (_panning)
        {
            var p = e.GetPosition(TopologyHost);
            TopologyPan.X += p.X - _panStart.X;
            TopologyPan.Y += p.Y - _panStart.Y;
            _panStart = p;
        }
    }

    private void Topology_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragNode = null;
        _panning = false;
        TopologyHost.ReleaseMouseCapture();
        TopologyHost.Cursor = Cursors.Arrow;
    }

    /// <summary>Zooms towards the pointer, so the thing under the cursor stays under the cursor.</summary>
    private void Topology_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var before = e.GetPosition(TopologyCanvas);          // in canvas coordinates
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        var next = Math.Clamp(TopologyScale.ScaleX * factor, 0.2, 3.0);
        if (Math.Abs(next - TopologyScale.ScaleX) < 0.0001) { e.Handled = true; return; }

        TopologyScale.ScaleX = TopologyScale.ScaleY = next;
        var after = e.GetPosition(TopologyCanvas);
        TopologyPan.X += (after.X - before.X) * next;
        TopologyPan.Y += (after.Y - before.Y) * next;
        e.Handled = true;
    }

    private async void Topology_Relayout_Click(object sender, RoutedEventArgs e)
    {
        if (_topoPhysical && !_tracing)
        {
            _traceResults = null;
            await RunTracesAsync();
        }
        BuildTopology(resetPositions: true);
        Topology_Fit_Click(this, new RoutedEventArgs());
    }

    /// <summary>Scales the graph until it fills the view (up to a sane maximum) and centres it.</summary>
    private void Topology_Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_topoNodes.Count == 0 || TopologyHost.ActualWidth < 50) return;
        double maxX = _topoNodes.Max(n => n.Position.X) + NodeWidth;
        double maxY = _topoNodes.Max(n => n.Position.Y) + NodeHeight;
        double minX = _topoNodes.Min(n => n.Position.X);
        double minY = _topoNodes.Min(n => n.Position.Y);

        double w = Math.Max(1, maxX - minX), h = Math.Max(1, maxY - minY);
        double scale = Math.Clamp(Math.Min((TopologyHost.ActualWidth - 40) / w,
                                           (TopologyHost.ActualHeight - 60) / h), 0.2, 1.6);
        TopologyScale.ScaleX = TopologyScale.ScaleY = scale;
        // Centre the scaled graph in both axes – no more hugging the left edge.
        TopologyPan.X = (TopologyHost.ActualWidth - w * scale) / 2 - minX * scale;
        TopologyPan.Y = (TopologyHost.ActualHeight - h * scale) / 2 - minY * scale;
    }
}
