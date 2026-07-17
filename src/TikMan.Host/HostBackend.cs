using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using TikMan.Web;

namespace TikMan.Host;

/// <summary>The headless implementation of <see cref="IWebBackend"/>: it owns a device list and drives
/// the same Core discovery/backup/SSH primitives the WPF app does, but without any UI thread. Where the
/// WPF backend marshals onto the dispatcher, this one guards its state with a plain lock – the web
/// server calls in from background threads either way.
/// <para>First cut on purpose: the device list, scan, details, Wake-on-LAN, credential setting, config
/// backup and the SSH terminal are live; the physical topology map (which the GUI derives from WPF
/// layout) is a flat logical graph here for now. Passwords are only ever used at send time and stored
/// solely via <see cref="CredentialProtector"/> – never logged.</para></summary>
public sealed class HostBackend : IWebBackend
{
    private readonly object _lock = new();
    private readonly List<Device> _devices = new();
    private readonly AppData _appData;
    private volatile bool _scanning;
    private double _progress;
    private string _phase = "";
    private int _scanned, _scanTotal;

    // Liveness per device id (WebId). Kept out of the persisted Device model on purpose – it is runtime
    // state, not settings. "" / missing = never checked ("Unknown").
    private readonly Dictionary<string, bool> _online = new();

    public HostBackend()
    {
        _appData = DeviceStore.Load();
        if (_appData.PersistDeviceList) _devices.AddRange(_appData.Devices);
        _ = Task.Run(MonitorLoopAsync); // background liveness, so the dashboard's status dots are live
    }

    /// <summary>Re-probes every device's reachability on a fixed cadence (and once at startup), so the
    /// status column reflects reality rather than staying "Unknown". A device is reachable when a TCP
    /// connect to one of its discovered open ports succeeds – privilege-free on every OS, unlike ICMP.</summary>
    private async Task MonitorLoopAsync()
    {
        while (true)
        {
            await ProbeAllAsync().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    }

    private async Task ProbeAllAsync()
    {
        List<Device> snapshot;
        lock (_lock) snapshot = _devices.ToList();
        foreach (var d in snapshot)
        {
            var port = d.OpenPorts.Count > 0 ? d.OpenPorts[0] : 0;
            var host = d.Host;
            bool up = port > 0 && await Reachability.TcpProbeAsync(host, port).ConfigureAwait(false);
            lock (_lock) _online[WebId(d)] = up;
        }
    }

    public string AppTitle => "TikMan";
    public string AppVersion => typeof(HostBackend).Assembly.GetName().Version?.ToString() ?? "0.0";

    // ---- device list ----------------------------------------------------------------------------

    public IReadOnlyList<DeviceDto> GetDevices()
    {
        lock (_lock) return _devices.Select(ToDto).ToList();
    }

    public DeviceDetail? GetDevice(string id)
    {
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return null;
            var info = d.ExtraInfo.Select(kv => new KeyVal(kv.Key, kv.Value)).ToList();
            return new DeviceDetail(WebId(d), Display(d), d.Host, d.MacAddress, Vendor(d), KindText(Kind(d)),
                Model(d), StatusText(d), d.EncryptedPassword.Length > 0, d.Username,
                d.MacAddress.Length > 0, VncPort(d), Ipv6Of(d), info);
        }
    }

    public ActionResult Wake(string id)
    {
        string mac;
        lock (_lock) mac = Find(id)?.MacAddress ?? "";
        if (mac.Length == 0) return new ActionResult(false, "no MAC for this device");
        var ok = WakeOnLan.Send(mac);
        return new ActionResult(ok, ok ? $"magic packet sent to {mac}" : "send failed");
    }

    public ActionResult SetLogin(string id, string user, string password)
    {
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return new ActionResult(false, "device gone");
            d.Username = user;
            d.EncryptedPassword = password.Length > 0 ? CredentialProtector.Protect(password) : "";
            Persist();
            return new ActionResult(true, "credentials updated");
        }
    }

    // ---- scanning -------------------------------------------------------------------------------

    public WebStatus GetStatus()
    {
        lock (_lock)
            return new WebStatus(_scanning, _scanning ? _progress : 0, _phase, _devices.Count);
    }

    public void StartScan()
    {
        lock (_lock) { if (_scanning) return; _scanning = true; _progress = -1; _phase = "Scanning"; _scanned = 0; _scanTotal = 0; }
        _ = Task.Run(RunScanAsync);
    }

    private async Task RunScanAsync()
    {
        try
        {
            var targets = LocalScanTargets();
            var onHost = new Progress<int>(_ =>
            {
                lock (_lock) { _scanned++; if (_scanTotal > 0) _progress = Math.Min(1.0, (double)_scanned / _scanTotal); }
            });
            _scanTotal = CountTargets(targets);
            var found = await SubnetScanner.ScanAsync(targets, onHostScanned: onHost,
                pingTimeoutMs: _appData.PingTimeoutMs);
            lock (_lock)
            {
                foreach (var f in found) Merge(f);
                Persist();
            }
            await ProbeAllAsync().ConfigureAwait(false); // fresh status right away, not up to 30s later
        }
        catch { /* a scan that fails leaves the last-known list in place */ }
        finally { lock (_lock) { _scanning = false; _phase = ""; _progress = 0; } }
    }

    /// <summary>Merges a freshly discovered host into the list, matched by MAC (or IP when it has no
    /// MAC), so a re-scan updates rather than duplicates.</summary>
    private void Merge(DiscoveredDevice f)
    {
        var existing = _devices.FirstOrDefault(d =>
            (f.MacAddress.Length > 0 && string.Equals(d.MacAddress, f.MacAddress, StringComparison.OrdinalIgnoreCase))
            || (f.MacAddress.Length == 0 && d.Host == f.IpAddress));
        var d = existing ?? new Device();
        d.Host = f.IpAddress;
        if (f.MacAddress.Length > 0) d.MacAddress = f.MacAddress;
        if (f.Identity.Length > 0 && d.Name.Length == 0) d.Name = f.Identity;
        if (f.Board.Length > 0) d.ExtraInfo["Modell"] = f.Board;
        if (f.Version.Length > 0) d.ExtraInfo["Version"] = f.Version;
        d.OpenPorts = f.OpenPorts.Distinct().OrderBy(p => p).ToList();
        if (existing is null) _devices.Add(d);
    }

    private void Persist()
    {
        if (!_appData.PersistDeviceList) return;
        _appData.Devices = _devices.ToList();
        try { DeviceStore.Save(_appData); } catch { /* best effort */ }
    }

    // ---- backup ---------------------------------------------------------------------------------

    public async Task<BackupResult> MakeBackupAsync(string id, bool full)
    {
        Device? d; string password;
        lock (_lock)
        {
            d = Find(id);
            if (d is null) return BackupResult.Fail("device gone");
            if (d.EncryptedPassword.Length == 0) return BackupResult.Fail("no stored login");
            password = CredentialProtector.Unprotect(d.EncryptedPassword);
        }
        if (password.Length == 0) return BackupResult.Fail("no stored login");

        try
        {
            if (full)
            {
                if (!IsMikroTik(d)) return BackupResult.Fail("binary backup is MikroTik-only");
                var tmp = Path.Combine(Path.GetTempPath(), "tikman-" + Guid.NewGuid().ToString("N") + ".backup");
                try
                {
                    await BackupService.DownloadFullBackupAsync(d, password, BackupMethod.Auto, d.SshPort, tmp);
                    var bytes = await File.ReadAllBytesAsync(tmp);
                    var name = BackupNaming.SuggestFileName(d.Name, Board(d), d.Host, DateTime.Now).Replace(".rsc", ".backup");
                    return new BackupResult(true, "", name, "application/octet-stream", bytes);
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }

            // Config export: Zyxel switch over its SSH CLI, RouterOS over /export (SSH – reliable even
            // when HTTPS is broken). The text can carry secrets, so it is streamed, never logged.
            string? config; string ext;
            if (IsZyxelSwitch(d))
            {
                config = await ZyxelSsh.GetRunningConfigAsync(d.Host, d.SshPort, d.Username, password);
                ext = ".cfg";
            }
            else
            {
                config = await SshConfigExport.GetAsync(d.Host, d.SshPort, d.Username, password);
                ext = ".rsc";
            }
            if (config is null) return BackupResult.Fail("config export failed (SSH)");
            var fn = BackupNaming.SuggestFileName(d.Name, Board(d), d.Host, DateTime.Now, ext);
            return new BackupResult(true, "", fn, "text/plain; charset=utf-8",
                System.Text.Encoding.UTF8.GetBytes(config));
        }
        catch (Exception ex) { return BackupResult.Fail("backup failed: " + ex.Message); }
    }

    // ---- SSH terminal / VNC ---------------------------------------------------------------------

    public async Task<ITerminalSession?> OpenSshShellAsync(string id, uint cols, uint rows)
    {
        Device? d; string password;
        lock (_lock)
        {
            d = Find(id);
            if (d is null || d.EncryptedPassword.Length == 0) return null;
            password = CredentialProtector.Unprotect(d.EncryptedPassword);
        }
        if (password.Length == 0) return null;
        return await SshTerminalSession.ConnectAsync(d.Host, d.SshPort, d.Username, password, cols, rows);
    }

    public NetEndpoint? GetVncTarget(string id)
    {
        lock (_lock)
        {
            var d = Find(id);
            var port = d is null ? 0 : VncPort(d);
            return port > 0 ? new NetEndpoint(d!.Host, port) : null;
        }
    }

    // ---- topology (logical only, headless) ------------------------------------------------------

    public Task<TopoGraph> GetTopologyAsync(bool physical)
    {
        // The GUI's physical map is derived from WPF layout + forwarding tables; headless we render a
        // flat logical view (Internet → devices). A proper Core-side layout is a later increment.
        lock (_lock)
        {
            var nodes = new List<TopoNodeDto>
            {
                new("internet", "", "Internet", "", "", 40, 20, 150, 46, "#dfe7f5", "#2266cc", "#1b1d21"),
            };
            var edges = new List<TopoEdgeDto>();
            int i = 0;
            foreach (var d in _devices)
            {
                var key = "d" + i;
                double x = 40 + i % 8 * 175.0, y = 120 + i / 8 * 90.0;
                var gw = Kind(d) is DeviceKind.Router or DeviceKind.Firewall;
                nodes.Add(new TopoNodeDto(key, WebId(d), Display(d), d.Host, d.MacAddress,
                    x, y, 160, 56, gw ? "#f7e2c4" : "#d9efdd", gw ? "#f68500" : "#2c9a4a", "#1b1d21"));
                edges.Add(new TopoEdgeDto("internet", key));
                i++;
            }
            return Task.FromResult(new TopoGraph(nodes, edges));
        }
    }

    // ---- mapping helpers ------------------------------------------------------------------------

    private Device? Find(string id) =>
        _devices.FirstOrDefault(d => WebId(d) == id);

    private static string WebId(Device d) => d.MacAddress.Length > 0 ? d.MacAddress : d.Host;
    private static string Display(Device d) => d.Name.Length > 0 ? d.Name : d.Host;

    private static string Vendor(Device d)
    {
        if (d.ExtraInfo.TryGetValue("Hersteller (Web)", out var w) && w.Length > 0) return w;
        var oui = OuiLookup.Lookup(d.MacAddress);
        return oui.Length > 0 ? oui : "";
    }

    private static string Model(Device d) =>
        d.ExtraInfo.TryGetValue("Modell", out var m) ? m : "";

    private static string Board(Device d) =>
        d.ExtraInfo.TryGetValue("Modell", out var m) ? m : "";

    private static bool IsMikroTik(Device d)
    {
        var oui = OuiLookup.Lookup(d.MacAddress).ToLowerInvariant();
        return oui.Contains("mikrotik") || oui.Contains("routerboard");
    }

    private static bool IsZyxelSwitch(Device d) =>
        Vendor(d).Contains("Zyxel", StringComparison.OrdinalIgnoreCase) && Kind(d) == DeviceKind.Switch;

    private static DeviceKind Kind(Device d) =>
        DeviceClassifier.Guess(Vendor(d), d.OpenPorts, Model(d), d.Name);

    private static int VncPort(Device d) =>
        d.OpenPorts.Contains(5900) ? 5900 : d.OpenPorts.Contains(5901) ? 5901 : 0;

    /// <summary>"Online"/"Offline" once the monitor has probed this device, else "Unknown" (grey dot) –
    /// e.g. a device restored from the saved list before the first probe, or one with no open port to
    /// test. Must be called under <see cref="_lock"/> (reads <see cref="_online"/>).</summary>
    private string StatusText(Device d) =>
        _online.TryGetValue(WebId(d), out var up) ? (up ? "Online" : "Offline") : "Unknown";

    private static List<string> Ipv6Of(Device d) => new();

    private DeviceDto ToDto(Device d) => new(
        WebId(d), Display(d), d.Host, d.MacAddress, Vendor(d), KindText(Kind(d)), Model(d),
        StatusText(d), Kind(d) is DeviceKind.Router or DeviceKind.Firewall, d.EncryptedPassword.Length > 0);

    /// <summary>English kind labels for the web UI (which is English). The GUI's localized names live
    /// in the WPF layer; duplicating a short map here keeps Core free of a display concern.</summary>
    private static string KindText(DeviceKind k) => k switch
    {
        DeviceKind.Router => "Router",
        DeviceKind.Firewall => "Firewall",
        DeviceKind.Switch => "Switch",
        DeviceKind.AccessPoint => "Access Point",
        DeviceKind.Printer => "Printer",
        DeviceKind.Nas => "NAS",
        DeviceKind.Pc => "PC",
        DeviceKind.Phone => "Phone",
        DeviceKind.Camera => "Camera",
        DeviceKind.IoT => "IoT",
        DeviceKind.Server => "Server",
        DeviceKind.Ups => "UPS",
        DeviceKind.Laptop => "Laptop",
        DeviceKind.Notebook => "Notebook",
        DeviceKind.Tablet => "Tablet",
        DeviceKind.PaymentTerminal => "Payment Terminal",
        _ => "",
    };

    // ---- local network → scan targets -----------------------------------------------------------

    /// <summary>Builds the scan target string from every up IPv4 interface: the local /24 around each
    /// address (a.b.c.1-254). A /24 keeps the sweep bounded and matches the typical LAN; a smarter
    /// prefix-aware range is a later refinement.</summary>
    private static string LocalScanTargets()
    {
        var ranges = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var b = ua.Address.GetAddressBytes();
                var range = $"{b[0]}.{b[1]}.{b[2]}.1-254";
                if (!ranges.Contains(range)) ranges.Add(range);
            }
        }
        return string.Join(",", ranges);
    }

    private static int CountTargets(string targets) =>
        targets.Split(',', StringSplitOptions.RemoveEmptyEntries).Sum(r => r.Contains('-') ? 254 : 1);
}
