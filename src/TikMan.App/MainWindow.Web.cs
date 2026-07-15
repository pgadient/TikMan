using System.Diagnostics;
using System.Windows;
using TikMan.App.Web;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>The built-in web server: a menu toggle plus the backend the server reads. The server runs
/// in-process and shares this window's live device list, so the web view always mirrors the GUI.</summary>
public partial class MainWindow : IWebBackend
{
    private WebServer? _webServer;

    /// <summary>Starts the server if it isn't already running. <paramref name="announce"/> is true when
    /// the user flipped the menu switch (so we surface problems), false on silent auto-start at launch.</summary>
    private void StartWebServer(bool announce)
    {
        if (_webServer?.IsRunning == true) { UpdateWebServerMenu(); return; }

        var user = _appData.WebServerUser?.Trim() ?? "";
        var pass = CredentialProtector.Unprotect(_appData.WebServerEncryptedPassword);
        if (user.Length == 0 || pass.Length == 0)
        {
            if (announce)
                MessageBox.Show(this, T("Web_NeedCreds"), T("Menu_WebServer"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateWebServerMenu();
            return;
        }

        var port = _appData.WebServerPort is > 0 and < 65536 ? _appData.WebServerPort : 9090;
        try
        {
            var server = new WebServer(this, port, user, pass);
            server.Start();
            _webServer = server;
            SetStatus(T("Web_StatusStarted", server.BoundUrl));
            if (server.LocalOnly && announce)
                MessageBox.Show(this, T("Web_LocalOnly", WebServer.ReservationCommand(port)),
                    T("Menu_WebServer"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _webServer = null;
            if (announce)
                MessageBox.Show(this, T("Web_StartFailed", ex.Message), T("Menu_WebServer"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateWebServerMenu();
    }

    private void StopWebServer()
    {
        _webServer?.Stop();
        _webServer = null;
        UpdateWebServerMenu();
    }

    private void UpdateWebServerMenu()
    {
        var running = _webServer?.IsRunning == true;
        WebServerToggleItem.IsChecked = running;
        WebServerOpenItem.IsEnabled = running;
        WebServerStatusItem.Header = running
            ? T(_webServer!.LocalOnly ? "Web_RunningLocal" : "Web_RunningLan", _webServer.BoundUrl)
            : T("Web_Stopped");
    }

    private void WebServerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_webServer?.IsRunning == true) StopWebServer();
        else StartWebServer(announce: true);
    }

    private void WebServerOpen_Click(object sender, RoutedEventArgs e)
    {
        var url = _webServer?.BoundUrl;
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked – the URL is shown in the menu for manual copy */ }
    }

    // ----- IWebBackend: what the web server reads from the running app -----

    string IWebBackend.AppTitle => "TikMan";
    string IWebBackend.AppVersion => CurrentBuild().Version.ToString();

    /// <summary>A snapshot of the live device list, taken on the UI thread (the web request runs on a
    /// background thread and must not touch the WPF-bound view models directly). Never includes any
    /// password – only whether the device has a stored login.</summary>
    WebStatus IWebBackend.GetStatus() => Dispatcher.Invoke(() =>
    {
        double progress = !_scanning ? 0
            : CombinedProgress.IsIndeterminate ? -1
            : _combinedShownPct / 100.0;
        var phase = _scanning ? CombinedProgressLabel.Text : "";
        return new WebStatus(_scanning, progress, phase, _devices.Count);
    });

    void IWebBackend.StartScan() => Dispatcher.Invoke(() =>
    {
        if (!_scanning) _ = RunDiscoveryAsync(auto: false);
    });

    IReadOnlyList<DeviceDto> IWebBackend.GetDevices() => Dispatcher.Invoke(() => _devices.Select(d =>
        new DeviceDto(
            Name: d.Name,
            Ip: d.Ipv4Address.Length > 0 ? d.Ipv4Address : d.Host,
            Mac: d.Model.MacAddress,
            Vendor: d.IdentifiedVendor,
            Type: d.DeviceType,
            Model: d.ModelDisplay,
            Status: d.StatusText,
            IsGateway: d.IsGateway,
            HasLogin: d.HasCredentials)).ToList());
}
