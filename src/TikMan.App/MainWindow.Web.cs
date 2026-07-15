using System.Diagnostics;
using System.IO;
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
            Status: d.StatusText, HasLogin: d.HasCredentials, User: d.Model.Username,
            CanWake: d.Model.MacAddress.Length > 0, Ipv6: d.Ipv6List, Info: info);
    });

    /// <summary>Applies a login to the device, mirroring the GUI's "set credentials": the password is
    /// only used transiently to DPAPI-encrypt it into the device (persisted to devices.json only when the
    /// user keeps the list), never logged. Called by the server exclusively over HTTPS.</summary>
    ActionResult IWebBackend.SetLogin(string id, string user, string password) => Dispatcher.Invoke(() =>
    {
        var d = FindByWebId(id);
        if (d is null) return new ActionResult(false, T("Web_DeviceGone"));
        d.Model.Username = (user ?? "").Trim();
        d.Model.EncryptedPassword = CredentialProtector.Protect(password ?? "");
        d.ResetClient();
        MarkGateways();
        SaveAppData();
        _ = AfterCredentialsChangedAsync(new[] { d });
        return new ActionResult(true, T("Web_LoginSet", d.Name.Length > 0 ? d.Name : d.Host));
    });

    ActionResult IWebBackend.Wake(string id) => Dispatcher.Invoke(() =>
    {
        var d = FindByWebId(id);
        if (d is null) return new ActionResult(false, T("Web_DeviceGone"));
        if (d.Model.MacAddress.Length == 0) return new ActionResult(false, T("Wol_NoMac", d.Host));
        var ok = WakeOnLan.Send(d.Model.MacAddress);
        return new ActionResult(ok, ok ? T("Wol_Sent", d.Model.MacAddress) : T("Wol_Failed", d.Host));
    });

    /// <summary>Produces a config export (.rsc) or full binary backup (.backup) for the device and
    /// returns its bytes to stream to the browser. Runs the backup on the UI thread (via the dispatcher,
    /// so its awaits don't freeze the GUI) exactly like the GUI's own backup buttons. The bytes are the
    /// file content – never logged. Server calls this over HTTPS only.</summary>
    async Task<BackupResult> IWebBackend.MakeBackupAsync(string id, bool full)
    {
        var vm = await Dispatcher.InvokeAsync(() => FindByWebId(id));
        if (vm is null) return BackupResult.Fail(T("Web_DeviceGone"));
        if (!vm.HasCredentials) return BackupResult.Fail(T("Web_BackupNoLogin"));

        var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
        try
        {
            if (!full)
            {
                var res = await await Dispatcher.InvokeAsync(() => vm.DownloadConfigAsync());
                if (res is not { } r) return BackupResult.Fail(T("Web_BackupFailed"));
                var name = BackupNaming.SuggestFileName(r.Identity, vm.Board, host, DateTime.Now);
                return new BackupResult(true, "", name, "text/plain; charset=utf-8",
                    System.Text.Encoding.UTF8.GetBytes(r.Config));
            }

            var method = _appData.BackupMethod;
            var sshPort = vm.Model.SshPort;
            var tempPath = Path.Combine(Path.GetTempPath(), "tikman-" + Guid.NewGuid().ToString("N") + ".backup");
            try
            {
                var ok = await await Dispatcher.InvokeAsync(() => vm.DownloadFullBackupAsync(method, sshPort, tempPath));
                if (!ok || !File.Exists(tempPath)) return BackupResult.Fail(T("Web_BackupFailed"));
                var bytes = await File.ReadAllBytesAsync(tempPath);
                var name = BackupNaming.SuggestFileName(vm.Name, vm.Board, host, DateTime.Now).Replace(".rsc", ".backup");
                return new BackupResult(true, "", name, "application/octet-stream", bytes);
            }
            finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
        }
        catch (Exception) { return BackupResult.Fail(T("Web_BackupFailed")); }
    }

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
