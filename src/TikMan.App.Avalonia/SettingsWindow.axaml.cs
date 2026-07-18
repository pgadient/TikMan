using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

public partial class SettingsWindow : Window
{
    // The settings this dialog edits. Snapshotted on open so «Abbrechen» reverts the live AppData
    // instance the fleet reads from (reflection keeps the two lists – XAML + here – from drifting).
    private static readonly string[] Fields =
    {
        "DefaultUsername", "PersistDeviceList", "NoInitialScan", "CheckForUpdates", "ExpandRowsByDefault",
        "SingleProgressBar", "SnmpCommunity", "PingTimeoutMs", "PingRetries", "SimpleScanMode",
        "AllowHttpFallback", "DefaultIgnoreCertErrors", "DefaultUpdateChannel", "UseExternalSshClient",
        "ExternalSshClientPath", "VlcPath", "WinScpPath",
    };

    private readonly AppData _appData = new();
    private readonly Dictionary<string, object?> _original = new();

    public SettingsWindow() => AvaloniaXamlLoader.Load(this); // XAML previewer

    public SettingsWindow(AppData appData)
    {
        AvaloniaXamlLoader.Load(this);
        _appData = appData;
        foreach (var f in Fields)
            if (typeof(AppData).GetProperty(f) is { } p) _original[f] = p.GetValue(appData);
        DataContext = appData;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try { DeviceStore.Save(_appData); } catch { /* a write failure shouldn't trap the user in the dialog */ }
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        foreach (var f in Fields)
            if (typeof(AppData).GetProperty(f) is { } p && _original.TryGetValue(f, out var v)) p.SetValue(_appData, v);
        Close();
    }
}
