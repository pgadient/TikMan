using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TikMan.Core.Discovery;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>The topology tab: the discovered devices drawn as a graph instead of a table.
///
/// A LAN scan cannot see the physical cabling – nothing short of reading every switch's bridge table
/// would – so the map does not pretend to. It draws what it *can* know for certain: the internet, the
/// gateway of each subnet, and every device hanging off that gateway, grouped by subnet and coloured
/// by kind. That is already what makes a customer's network legible at a glance, and the nodes can be
/// dragged into the shape the site actually has.</summary>
public partial class MainWindow
{
    private const double NodeWidth = 168, NodeHeight = 56, ColGap = 22, RowGap = 18;

    private readonly Dictionary<string, Point> _topoPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TopoNode> _topoNodes = new();
    private readonly List<TopoEdge> _topoEdges = new();

    private TopoNode? _dragNode;
    private Point _dragOffset;
    private Point _panStart;
    private bool _panning;

    private sealed class TopoNode
    {
        public required string Key { get; init; }              // MAC, or a synthetic key for the pseudo-nodes
        public required Border Visual { get; init; }
        public DeviceViewModel? Device { get; init; }          // null for "Internet" / a bare subnet
        public Point Position { get; set; }
    }

    private sealed class TopoEdge
    {
        public required Line Visual { get; init; }
        public required TopoNode From { get; init; }
        public required TopoNode To { get; init; }
    }

    // ---- building -----------------------------------------------------------------------------

    /// <summary>Rebuilds the map from the current device list, keeping any node the user has already
    /// dragged where they put it.</summary>
    private void BuildTopology(bool resetPositions = false)
    {
        if (resetPositions) _topoPositions.Clear();
        TopologyCanvas.Children.Clear();
        _topoNodes.Clear();
        _topoEdges.Clear();

        var devices = _devices.Where(d => d.HasIpv4).ToList();
        if (devices.Count == 0) return;

        // Group by /24. The gateway is the device that routes for that subnet – a router or firewall,
        // or failing that whatever sits on .1, which is where a gateway lives often enough to be a
        // better guess than none.
        var subnets = devices
            .GroupBy(Subnet)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var internet = AddNode("::internet", null, T("Topo_Internet"), "", "", "#37474F");

        double x = 0;
        double topRow = 120, gearRow = 240, clientRow = 360;

        foreach (var subnet in subnets)
        {
            var members = subnet.ToList();
            var gateway = members.FirstOrDefault(IsGateway)
                          ?? members.FirstOrDefault(d => d.Ipv4Address.EndsWith(".1", StringComparison.Ordinal));

            // Infrastructure (switches, APs) gets its own row, so the client cloud below stays readable.
            var gear = members.Where(d => d != gateway && IsInfrastructure(d)).ToList();
            var clients = members.Where(d => d != gateway && !IsInfrastructure(d)).ToList();

            var gwNode = gateway is not null
                ? AddDeviceNode(gateway)
                : AddNode("::net:" + subnet.Key, null, subnet.Key + ".0/24", "", "", "#78909C");
            Place(gwNode, x + ColumnWidth(clients.Count) / 2 - NodeWidth / 2, topRow);
            Connect(internet, gwNode);

            double gx = x;
            foreach (var d in gear)
            {
                var n = AddDeviceNode(d);
                Place(n, gx, gearRow);
                Connect(gwNode, n);
                gx += NodeWidth + ColGap;
            }

            // Clients in a block under their gateway – as many columns as fit the widest row.
            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(clients.Count)));
            for (int i = 0; i < clients.Count; i++)
            {
                var n = AddDeviceNode(clients[i]);
                Place(n, x + i % cols * (NodeWidth + ColGap),
                         clientRow + i / cols * (NodeHeight + RowGap));
                Connect(gwNode, n);
            }

            var used = Math.Max(ColumnWidth(clients.Count), gear.Count * (NodeWidth + ColGap));
            x += Math.Max(used, NodeWidth + ColGap) + 70;   // a lane of air between subnets
        }

        Place(internet, Math.Max(0, x / 2 - NodeWidth / 2), 20);
        RedrawEdges();
    }

    private static double ColumnWidth(int clientCount)
    {
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(clientCount)));
        return cols * (NodeWidth + ColGap);
    }

    private static string Subnet(DeviceViewModel d)
    {
        var parts = d.Ipv4Address.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts[..3]) : d.Ipv4Address;
    }

    private static bool IsGateway(DeviceViewModel d) =>
        d.KindOf() is DeviceKind.Router or DeviceKind.Firewall;

    private static bool IsInfrastructure(DeviceViewModel d) =>
        d.KindOf() is DeviceKind.Switch or DeviceKind.AccessPoint;

    // ---- nodes --------------------------------------------------------------------------------

    private TopoNode AddDeviceNode(DeviceViewModel d)
    {
        var key = d.Model.MacAddress.Length > 0 ? d.Model.MacAddress : d.Ipv4Address;
        var title = d.Name.Length > 0 ? d.Name : d.Ipv4Address;
        return AddNode(key, d, title, d.Ipv4Address, d.Model.MacAddress, ColourFor(d.KindOf()), d.DeviceType);
    }

    private TopoNode AddNode(string key, DeviceViewModel? device, string title, string ip, string mac,
        string colour, string kind = "")
    {
        var accent = (SolidColorBrush)new BrushConverter().ConvertFromString(colour)!;
        accent.Freeze();

        var stack = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detail = string.Join("  ·  ", new[] { ip, kind }.Where(s => s.Length > 0));
        if (detail.Length > 0)
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 10, Foreground = accent });
        if (mac.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = mac, FontSize = 9, Foreground = Brushes.Gray,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

        var border = new Border
        {
            Width = NodeWidth,
            MinHeight = NodeHeight,
            Background = Brushes.White,
            BorderBrush = accent,
            BorderThickness = new Thickness(1, 1, 1, 3),
            CornerRadius = new CornerRadius(6),
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
        // A node the user has already dragged stays where they put it, even across a rescan.
        var p = _topoPositions.TryGetValue(node.Key, out var saved) ? saved : new Point(x, y);
        node.Position = p;
        _topoPositions[node.Key] = p;
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

    private static string ColourFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Router => "#2B6CB0",
        DeviceKind.Firewall => "#C0392B",
        DeviceKind.Switch => "#2B3A42",
        DeviceKind.AccessPoint => "#1E8449",
        DeviceKind.Server => "#6C3483",
        DeviceKind.Management => "#5D4037",
        DeviceKind.Nas => "#16A085",
        DeviceKind.Printer => "#A0522D",
        DeviceKind.Camera => "#00838F",
        DeviceKind.Phone or DeviceKind.Smartphone => "#C2185B",
        DeviceKind.Tv or DeviceKind.Audio => "#8E44AD",
        DeviceKind.GameConsole => "#E67E22",
        DeviceKind.IoT => "#7F8C8D",
        DeviceKind.Ups => "#B7950B",
        DeviceKind.PaymentTerminal or DeviceKind.Franking => "#795548",
        _ => "#95A5A6",
    };

    // ---- interaction --------------------------------------------------------------------------

    private void Topology_NodeDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: TopoNode node }) return;
        _dragNode = node;
        var p = e.GetPosition(TopologyCanvas);
        _dragOffset = new Point(p.X - node.Position.X, p.Y - node.Position.Y);
        TopologyCanvas.CaptureMouse();

        if (node.Device is { } vm) DeviceGrid.SelectedItem = vm; // clicking a node selects it in the list
        e.Handled = true;
    }

    private void Topology_BackgroundDown(object sender, MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(TopologyHost);
        TopologyCanvas.CaptureMouse();
    }

    private void Topology_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is { } node)
        {
            var p = e.GetPosition(TopologyCanvas);
            var pos = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
            node.Position = pos;
            _topoPositions[node.Key] = pos;
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
        TopologyCanvas.ReleaseMouseCapture();
    }

    private void Topology_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var next = Math.Clamp(TopologyScale.ScaleX * factor, 0.25, 3.0);
        TopologyScale.ScaleX = TopologyScale.ScaleY = next;
        e.Handled = true;
    }

    private void Topology_Relayout_Click(object sender, RoutedEventArgs e) => BuildTopology(resetPositions: true);

    /// <summary>Zooms out until everything fits, and pans the whole graph back into view.</summary>
    private void Topology_Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_topoNodes.Count == 0) return;
        double maxX = _topoNodes.Max(n => n.Position.X) + NodeWidth;
        double maxY = _topoNodes.Max(n => n.Position.Y) + NodeHeight;
        double minX = _topoNodes.Min(n => n.Position.X);
        double minY = _topoNodes.Min(n => n.Position.Y);

        double w = Math.Max(1, maxX - minX), h = Math.Max(1, maxY - minY);
        double scale = Math.Clamp(Math.Min((TopologyHost.ActualWidth - 40) / w,
                                           (TopologyHost.ActualHeight - 60) / h), 0.25, 1.5);
        TopologyScale.ScaleX = TopologyScale.ScaleY = scale;
        TopologyPan.X = 20 - minX * scale;
        TopologyPan.Y = 20 - minY * scale;
    }
}
