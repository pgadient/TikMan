using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using TikMan.Core.Discovery;

namespace TikMan.App.Avalonia;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm = new();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _vm;
    }

    private void OnScanClick(object? sender, RoutedEventArgs e) => _vm.Scan();
    private void OnWakeClick(object? sender, RoutedEventArgs e) => _vm.Wake();

    private void OnShowList(object? sender, RoutedEventArgs e)
    {
        ListView.IsVisible = true;
        MapScroller.IsVisible = false;
    }

    private void OnShowLogical(object? sender, RoutedEventArgs e) => Draw(_vm.BuildLogicalTopology());

    private async void OnShowPhysical(object? sender, RoutedEventArgs e)
    {
        ListView.IsVisible = false;
        MapScroller.IsVisible = true;
        MapCanvas.Children.Clear();
        MapCanvas.Children.Add(new TextBlock
        {
            Text = "Karte wird erstellt… (Forwarding-Tabellen werden gelesen)",
            Margin = new Thickness(24),
        });
        try { Draw(await _vm.BuildPhysicalTopologyAsync()); }
        catch (Exception ex)
        {
            MapCanvas.Children.Clear();
            MapCanvas.Children.Add(new TextBlock { Text = "Karte fehlgeschlagen: " + ex.Message, Margin = new Thickness(24) });
        }
    }

    /// <summary>Renders a topology layout onto the canvas: edges as lines behind the boxes, each node as a
    /// coloured rounded rectangle at its computed position. The colours are the shared hex strings the Core
    /// builder produced, so the map looks the same here, in the web UI and in the WPF/PDF export.</summary>
    private void Draw(TopoLayout layout)
    {
        ListView.IsVisible = false;
        MapScroller.IsVisible = true;
        MapCanvas.Children.Clear();

        var byKey = new Dictionary<string, TopoBox>();
        foreach (var n in layout.Nodes) byKey[n.Key] = n;

        foreach (var e in layout.Edges)
        {
            if (!byKey.TryGetValue(e.From, out var a) || !byKey.TryGetValue(e.To, out var b)) continue;
            MapCanvas.Children.Add(new Line
            {
                StartPoint = new Point(a.X + a.W / 2, a.Y + a.H / 2),
                EndPoint = new Point(b.X + b.W / 2, b.Y + b.H / 2),
                Stroke = Brush("#C4CCD2"),
                StrokeThickness = 1.4,
            });
        }

        double maxX = 0, maxY = 0;
        foreach (var n in layout.Nodes)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = n.Title,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(n.Text),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrEmpty(n.Detail))
                stack.Children.Add(new TextBlock { Text = n.Detail, FontSize = 11, Foreground = Brush(n.Text), Opacity = 0.85 });

            var box = new Border
            {
                Width = n.W,
                Height = n.H,
                Background = Brush(n.Fill),
                BorderBrush = Brush(n.Line),
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 6),
                Child = stack,
            };
            Canvas.SetLeft(box, n.X);
            Canvas.SetTop(box, n.Y);
            MapCanvas.Children.Add(box);

            maxX = Math.Max(maxX, n.X + n.W);
            maxY = Math.Max(maxY, n.Y + n.H);
        }

        MapCanvas.Width = maxX + 40;
        MapCanvas.Height = maxY + 40;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
