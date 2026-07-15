using System.Diagnostics;
using System.Windows;
using TikMan.App.Web;
using TikMan.Core.Discovery;
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

        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
        if (_appData.WebServerUseHttps)
        {
            try
            {
                cert = WebCertificate.LoadOrCreate(_appData.WebServerCertPath?.Trim() ?? "",
                    CredentialProtector.Unprotect(_appData.WebServerCertPassword));
            }
            catch (Exception ex)
            {
                if (announce)
                    MessageBox.Show(this, T("Web_CertFailed", ex.Message), T("Menu_WebServer"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateWebServerMenu();
                return;
            }
        }

        try
        {
            var server = new WebServer(this, port, user, pass, cert);
            server.Start();
            _webServer = server;
            SetStatus(T("Web_StatusStarted", server.BoundUrl));
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
            ? T("Web_RunningLan", _webServer!.BoundUrl)
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

    IReadOnlyList<DeviceDto> IWebBackend.GetDevices() =>
        Dispatcher.Invoke(() => _devices.Select(ToDto).ToList());

    DeviceDetail? IWebBackend.GetDevice(string id) => Dispatcher.Invoke(() =>
    {
        var d = FindByWebId(id);
        if (d is null) return null;
        var info = d.ExtraInfo.Select(r => new KeyVal(r.Key, r.Value)).ToList();
        return new DeviceDetail(
            Id: WebId(d), Name: d.Name, Ip: d.Ipv4Address.Length > 0 ? d.Ipv4Address : d.Host,
            Mac: d.Model.MacAddress, Vendor: d.IdentifiedVendor, Type: d.DeviceType, Model: d.ModelDisplay,
            Status: d.StatusText, HasLogin: d.HasCredentials, CanWake: d.Model.MacAddress.Length > 0,
            Ipv6: d.Ipv6List, Info: info);
    });

    ActionResult IWebBackend.Wake(string id) => Dispatcher.Invoke(() =>
    {
        var d = FindByWebId(id);
        if (d is null) return new ActionResult(false, T("Web_DeviceGone"));
        if (d.Model.MacAddress.Length == 0) return new ActionResult(false, T("Wol_NoMac", d.Host));
        var ok = WakeOnLan.Send(d.Model.MacAddress);
        return new ActionResult(ok, ok ? T("Wol_Sent", d.Model.MacAddress) : T("Wol_Failed", d.Host));
    });

    private DeviceDto ToDto(DeviceViewModel d) => new(
        Id: WebId(d),
        Name: d.Name,
        Ip: d.Ipv4Address.Length > 0 ? d.Ipv4Address : d.Host,
        Mac: d.Model.MacAddress,
        Vendor: d.IdentifiedVendor,
        Type: d.DeviceType,
        Model: d.ModelDisplay,
        Status: d.StatusText,
        IsGateway: d.IsGateway,
        HasLogin: d.HasCredentials);

    /// <summary>A stable id for the web layer: the MAC (the app's real device identity) when known,
    /// otherwise the host address. Only used to map a web request back to a live device.</summary>
    private static string WebId(DeviceViewModel d) =>
        d.Model.MacAddress.Length > 0 ? d.Model.MacAddress : d.Host;

    private DeviceViewModel? FindByWebId(string id) =>
        _devices.FirstOrDefault(d => string.Equals(WebId(d), id, StringComparison.OrdinalIgnoreCase));
}
