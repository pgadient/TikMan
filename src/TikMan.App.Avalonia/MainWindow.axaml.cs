using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

    private async void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        await new SettingsWindow(_vm.Settings).ShowDialog(this);

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        var device = _vm.SelectedDevice;
        if (device is null) return;
        var dlg = new LoginWindow(device.Name, _vm.LoginUserFor(device));
        if (await dlg.ShowDialog<LoginResult?>(this) is { } result)
            _vm.SetLogin(result.User, result.Password);
    }

    private async void OnBackupClick(object? sender, RoutedEventArgs e)
    {
        _vm.ReportAction("Backup läuft…");
        var backup = await _vm.BackupConfigAsync();
        if (!backup.Ok) { _vm.ReportAction(backup.Message); return; }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = backup.FileName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Konfiguration") { Patterns = new[] { "*.rsc", "*.cfg" } },
            },
        });
        if (file is null) { _vm.ReportAction("Backup abgebrochen."); return; }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(backup.Bytes);
            _vm.ReportAction("Backup gespeichert: " + backup.FileName);
        }
        catch (System.Exception ex) { _vm.ReportAction("Speichern fehlgeschlagen: " + ex.Message); }
    }

    /// <summary>Draws the topology when its tab is selected: the logical map is instant, the physical one
    /// reads the forwarding tables (async, with a loading hint). The device tab needs no work.</summary>
    private async void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Tabs is null) return; // fires once during construction, before the fields are set
        try
        {
            switch (Tabs.SelectedIndex)
            {
                case 1:
                    Draw(_vm.BuildLogicalTopology(), LogicalCanvas);
                    break;
                case 2:
                    ShowLoading(PhysicalCanvas);
                    Draw(await _vm.BuildPhysicalTopologyAsync(), PhysicalCanvas);
                    break;
            }
        }
        catch (Exception ex)
        {
            var canvas = Tabs.SelectedIndex == 2 ? PhysicalCanvas : LogicalCanvas;
            canvas.Children.Clear();
            canvas.Children.Add(new TextBlock { Text = "Karte fehlgeschlagen: " + ex.Message, Margin = new Thickness(24) });
        }
    }

    private static void ShowLoading(Canvas canvas)
    {
        canvas.Children.Clear();
        canvas.Children.Add(new TextBlock
        {
            Text = "Karte wird erstellt… (Forwarding-Tabellen werden gelesen)",
            Margin = new Thickness(24),
        });
    }

    /// <summary>Renders a topology layout onto a canvas: edges as lines behind the boxes, each node as a
    /// coloured rounded rectangle at its computed position. The colours are the shared hex strings the Core
    /// builder produced, so the map looks the same here, in the web UI and in the WPF/PDF export.</summary>
    private static void Draw(TopoLayout layout, Canvas canvas)
    {
        canvas.Children.Clear();

        var byKey = new Dictionary<string, TopoBox>();
        foreach (var n in layout.Nodes) byKey[n.Key] = n;

        foreach (var edge in layout.Edges)
        {
            if (!byKey.TryGetValue(edge.From, out var a) || !byKey.TryGetValue(edge.To, out var b)) continue;
            canvas.Children.Add(new Line
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
            canvas.Children.Add(box);

            maxX = Math.Max(maxX, n.X + n.W);
            maxY = Math.Max(maxY, n.Y + n.H);
        }

        canvas.Width = maxX + 40;
        canvas.Height = maxY + 40;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
