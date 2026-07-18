using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

public partial class SettingsWindow : Window
{
    private readonly AppData _appData = new();
    private readonly (string Snmp, int Timeout, int Retries, string User, bool Persist, bool Http) _original;

    public SettingsWindow() => AvaloniaXamlLoader.Load(this); // XAML previewer

    public SettingsWindow(AppData appData)
    {
        AvaloniaXamlLoader.Load(this);
        _appData = appData;
        // Remember the fields we expose, so «Abbrechen» reverts the live instance we bind to.
        _original = (appData.SnmpCommunity, appData.PingTimeoutMs, appData.PingRetries,
                     appData.DefaultUsername, appData.PersistDeviceList, appData.AllowHttpFallback);
        DataContext = appData;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try { DeviceStore.Save(_appData); } catch { /* keep the dialog open on a write failure is overkill; ignore */ }
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _appData.SnmpCommunity = _original.Snmp;
        _appData.PingTimeoutMs = _original.Timeout;
        _appData.PingRetries = _original.Retries;
        _appData.DefaultUsername = _original.User;
        _appData.PersistDeviceList = _original.Persist;
        _appData.AllowHttpFallback = _original.Http;
        Close();
    }
}
