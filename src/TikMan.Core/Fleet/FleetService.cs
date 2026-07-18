using System.Net;
using System.Net.NetworkInformation;
using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;

namespace TikMan.Core.Fleet;

/// <summary>A computed, UI-free view of one device – everything a list needs, already classified. The
/// raw <see cref="Device"/> stays with <see cref="FleetService"/>; this is the read model.</summary>
public sealed record DeviceSnapshot(
    string Id, string Name, string Ip, string Mac, string Vendor, DeviceKind Kind, string KindText,
    string Model, string Status, bool IsGateway, bool HasLogin, int VncPort, string User,
    IReadOnlyList<KeyValuePair<string, string>> Info);

/// <summary>The shared device inventory: it scans the network, enriches and classifies the results, keeps
/// each device's reachability current, and persists the list – all UI-free, so the headless host and the
/// Avalonia GUI (and, in time, WPF) run on the same engine instead of three copies of it.
/// <para>Thread-safe: every read hands back a snapshot copy, every mutation is under one lock. Callers
/// subscribe to <see cref="Changed"/> to refresh their view. Passwords are only ever used at send time
/// and stored solely via <see cref="CredentialProtector"/> – never logged.</para></summary>
public sealed class FleetService
{
    private readonly object _lock = new();
    private readonly List<Device> _devices = new();
    private readonly Dictionary<string, bool> _online = new(); // WebId → reachable; missing = "Unknown"
    private readonly AppData _appData;
    private volatile bool _scanning;
    private double _progress;
    private string _phase = "";
    private int _scanned, _scanTotal;

    /// <summary>Raised (on a background thread) whenever the list or scan state changed – bind your UI to it.</summary>
    public event Action? Changed;

    public FleetService(AppData appData)
    {
        _appData = appData;
        if (_appData.PersistDeviceList) _devices.AddRange(_appData.Devices);
        _ = Task.Run(MonitorLoopAsync);
    }

    // ---- reads -----------------------------------------------------------------------------------

    public IReadOnlyList<DeviceSnapshot> Snapshot()
    {
        lock (_lock) return _devices.Select(ToSnapshot).ToList();
    }

    public DeviceSnapshot? SnapshotOf(string id)
    {
        lock (_lock) { var d = Find(id); return d is null ? null : ToSnapshot(d); }
    }

    /// <summary>The raw device for host-side operations (backup/SSH/VNC/topology need host+creds+ports).</summary>
    public Device? RawDevice(string id) { lock (_lock) return Find(id); }
    public IReadOnlyList<Device> RawDevices() { lock (_lock) return _devices.ToList(); }

    public (bool Scanning, double Progress, string Phase, int Count) Status
    {
        get { lock (_lock) return (_scanning, _scanning ? _progress : 0, _phase, _devices.Count); }
    }

    // ---- writes ----------------------------------------------------------------------------------

    /// <summary>Sets (or clears) a device's login. Returns false if the device is gone.</summary>
    public bool SetLogin(string id, string user, string password)
    {
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return false;
            d.Username = user;
            d.EncryptedPassword = password.Length > 0 ? CredentialProtector.Protect(password) : "";
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    public void StartScan()
    {
        lock (_lock)
        {
            if (_scanning) return;
            _scanning = true; _progress = -1; _phase = "Scanning"; _scanned = 0; _scanTotal = 0;
        }
        Changed?.Invoke();
        _ = Task.Run(RunScanAsync);
    }

    // ---- scanning + enrichment -------------------------------------------------------------------

    private async Task RunScanAsync()
    {
        try
        {
            var targets = LocalScanTargets();
            var onHost = new Progress<int>(_ =>
            {
                lock (_lock) { _scanned++; if (_scanTotal > 0) _progress = Math.Min(1.0, (double)_scanned / _scanTotal); }
                Changed?.Invoke();
            });
            _scanTotal = CountTargets(targets);
            var found = await SubnetScanner.ScanAsync(targets, onHostScanned: onHost,
                pingTimeoutMs: _appData.PingTimeoutMs);
            lock (_lock) { foreach (var f in found) Merge(f); Persist(); }
            Changed?.Invoke();
            await EnrichAsync(CancellationToken.None).ConfigureAwait(false);
            await ProbeAllAsync().ConfigureAwait(false);
        }
        catch { /* a scan that fails leaves the last-known list in place */ }
        finally
        {
            lock (_lock) { _scanning = false; _phase = ""; _progress = 0; }
            Changed?.Invoke();
        }
    }

    private void Merge(DiscoveredDevice f)
    {
        var existing = _devices.FirstOrDefault(d =>
            (f.MacAddress.Length > 0 && string.Equals(d.MacAddress, f.MacAddress, StringComparison.OrdinalIgnoreCase))
            || (f.MacAddress.Length == 0 && d.Host == f.IpAddress));
        var d = existing ?? new Device();
        d.Host = f.IpAddress;
        if (f.MacAddress.Length > 0) d.MacAddress = f.MacAddress;
        if (f.Identity.Length > 0 && d.Name.Length == 0) d.Name = f.Identity;
        if (f.Board.Length > 0)
        {
            d.ExtraInfo["Modell"] = f.Board;
            d.ExtraInfo["Hersteller (Web)"] = "MikroTik"; // MNDP announces only from MikroTik
        }
        else if (f.IsLikelyMikroTik && !d.ExtraInfo.ContainsKey("Hersteller (Web)"))
            d.ExtraInfo["Hersteller (Web)"] = "MikroTik";
        if (f.Version.Length > 0) d.ExtraInfo["Version"] = f.Version;
        if (f.OpenPorts.Count > 0) d.OpenPorts = f.OpenPorts.Distinct().OrderBy(p => p).ToList();
        if (existing is null) _devices.Add(d);
    }

    /// <summary>Names devices the TCP scan can't: MNDP (MikroTik board), mDNS/SSDP (Apple/Sonos/TV), then
    /// per-device probes (web/TLS/SNMP/SMB/vendor-SSH). All best-effort, no login where possible.</summary>
    private async Task EnrichAsync(CancellationToken ct)
    {
        var mndpTask = Safe(() => MndpScanner.DiscoverAsync(TimeSpan.FromSeconds(4), null, ct), new List<DiscoveredDevice>());
        var mdnsTask = Safe(() => MdnsScanner.DiscoverAsync(TimeSpan.FromSeconds(4), ct), new Dictionary<string, MdnsScanner.MdnsInfo>());
        var ssdpTask = Safe(() => SsdpScanner.DiscoverAsync(TimeSpan.FromSeconds(4), ct), new Dictionary<string, SsdpScanner.SsdpInfo>());
        await Task.WhenAll(mndpTask, mdnsTask, ssdpTask).ConfigureAwait(false);

        string community;
        lock (_lock)
        {
            community = _appData.SnmpCommunity?.Trim() is { Length: > 0 } c ? c : "public";
            foreach (var f in mndpTask.Result) Merge(f);
            foreach (var d in _devices)
            {
                if (mdnsTask.Result.TryGetValue(d.Host, out var m)) ApplyMdns(d, m);
                if (ssdpTask.Result.TryGetValue(d.Host, out var s)) ApplySsdp(d, s);
            }
            Persist();
        }
        Changed?.Invoke();

        await ProbeDevicesAsync(community, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private async Task ProbeDevicesAsync(string community, CancellationToken ct)
    {
        List<Device> snapshot;
        lock (_lock) snapshot = _devices.ToList();
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(snapshot.Select(async d =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await ProbeOneDeviceAsync(d, community, ct).ConfigureAwait(false); }
            catch { /* best-effort per device */ }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        lock (_lock) Persist();
    }

    private async Task ProbeOneDeviceAsync(Device d, string community, CancellationToken ct)
    {
        var host = d.Host;
        if (host.Length == 0) return;
        var ports = d.OpenPorts;
        bool web = ports.Any(p => p is 80 or 443 or 8080);

        if (web && await QnapProbe.QueryAsync(host, ports, ct).ConfigureAwait(false) is { } qnap)
        {
            lock (_lock)
            {
                d.ExtraInfo["Hersteller (Web)"] = "QNAP";
                if (qnap.Os.Length > 0) d.ExtraInfo["System"] = qnap.Os;
                if (qnap.Model.Length > 0) d.ExtraInfo["Modell"] = qnap.Model;
                if (qnap.Hostname.Length > 0 && d.Name.Length == 0) d.Name = qnap.Hostname;
            }
            return;
        }

        if (web)
        {
            var fp = await HttpFingerprint.ProbeAsync(host, ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (fp.WebServer.Length > 0 && !d.ExtraInfo.ContainsKey("Webserver")) d.ExtraInfo["Webserver"] = fp.WebServer;
                if (fp.Title.Length > 0 && !d.ExtraInfo.ContainsKey("Web-Titel")) d.ExtraInfo["Web-Titel"] = fp.Title;
                if (fp.Vendor.Length > 0 && !d.ExtraInfo.ContainsKey("Hersteller (Web)")) d.ExtraInfo["Hersteller (Web)"] = fp.Vendor;
            }
        }

        if (ports.Contains(443) && await TlsCertProbe.QueryAsync(host, 443, ct).ConfigureAwait(false) is { } tls)
        {
            lock (_lock)
            {
                if (!d.ExtraInfo.ContainsKey("Hersteller (Web)"))
                {
                    var brand = ModelVendor.FromModel(tls.Model);
                    if (brand.Length == 0) brand = ModelVendor.FromModel(tls.Vendor);
                    if (brand.Length == 0 && tls.Vendor.Length is > 0 and <= 30) brand = tls.Vendor;
                    if (brand.Length > 0) d.ExtraInfo["Hersteller (Web)"] = brand;
                }
                if (tls.Model.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = tls.Model;
                if (tls.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = tls.Serial;
                if (tls.Mac.Length > 0 && d.MacAddress.Length == 0) d.MacAddress = tls.Mac;
            }
        }

        var hint = VendorHint(d);
        if (web && hint.Contains("brother") && await BrotherProbe.QueryAsync(host, ct).ConfigureAwait(false) is { } br)
            lock (_lock)
            {
                if (br.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = br.Serial;
                if (br.MainFirmware.Length > 0) d.ExtraInfo["Firmware"] = br.MainFirmware;
                foreach (var kv in br.SubFirmware) d.ExtraInfo[kv.Key] = kv.Value;
            }
        if (web && (hint.Contains("swisscom") || hint.Replace("-", "").Replace(" ", "").Contains("internetbox")) &&
            await SwisscomProbe.QueryAsync(host, ct).ConfigureAwait(false) is { } box)
            lock (_lock)
            {
                if (box.ModelName.Length > 0) d.ExtraInfo["Modell"] = box.ModelName;
                if (box.Serial.Length > 0) d.SerialNumber = box.Serial;
                if (box.Firmware.Length > 0) d.ExtraInfo["Firmware"] = box.Firmware;
            }
        if (web && (hint.Contains("frontier") || hint.Contains("internet radio")) &&
            await FrontierSiliconProbe.QueryAsync(host, ct).ConfigureAwait(false) is { } radio)
            lock (_lock)
            {
                if (radio.Name.Length > 0) d.ExtraInfo["Modell"] = radio.Name;
                if (radio.Vendor.Length > 0 && !d.ExtraInfo.ContainsKey("Hersteller (Web)")) d.ExtraInfo["Hersteller (Web)"] = radio.Vendor;
                if (radio.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = radio.Serial;
                if (radio.Firmware.Length > 0) d.ExtraInfo["Firmware"] = radio.Firmware;
            }

        if (ports.Contains(445) && await SmbInfoProbe.QueryAsync(host, 445, ct).ConfigureAwait(false) is { } smb)
            lock (_lock)
            {
                if (smb.ComputerName.Length > 0 && d.Name.Length == 0) d.Name = smb.ComputerName;
                if (smb.OsFriendly.Length > 0 && !d.ExtraInfo.ContainsKey("System")) d.ExtraInfo["System"] = smb.OsFriendly;
            }

        if (await SnmpProbe.QueryAsync(host, ct, community).ConfigureAwait(false) is { } snmp)
            lock (_lock)
            {
                if (snmp.SysName.Length > 0 && d.Name.Length == 0) d.Name = snmp.SysName;
                if (snmp.SysDescr.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = snmp.SysDescr;
            }

        if (ports.Contains(22) && d.EncryptedPassword.Length > 0 && d.Username.Trim().Length > 0)
        {
            var password = CredentialProtector.Unprotect(d.EncryptedPassword);
            if (password.Length > 0 &&
                await SshInfoProbe.QueryAsync(host, d.SshPort, d.Username.Trim(), password, hint, ct).ConfigureAwait(false) is { } ssh)
                lock (_lock)
                {
                    if (ssh.Model.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = ssh.Model;
                    if (ssh.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = ssh.Serial;
                    if (ssh.Firmware.Length > 0 && !d.ExtraInfo.ContainsKey("Firmware")) d.ExtraInfo["Firmware"] = ssh.Firmware;
                }
        }

        try
        {
            if (await DnsProbe.IsOpenAsync(host, ct).ConfigureAwait(false))
                lock (_lock) { if (!d.OpenPorts.Contains(53)) d.OpenPorts.Add(53); }
        }
        catch { /* best effort */ }
    }

    // ---- topology (shared by the headless host and the GUI) --------------------------------------

    /// <summary>The flat logical map (Internet → devices) – instant, no forwarding tables needed.</summary>
    public TopoLayout BuildLogicalTopology() => PhysicalTopology.BuildLogical(TopoInputs());

    /// <summary>The physical map from the bridge forwarding tables (who sees which MAC on which port),
    /// plus WLAN SSIDs and traced routes. Slow – it talks to every infrastructure device – so only build
    /// it on demand. Gateway from <see cref="TraceRoute.DefaultGateway"/> (cross-platform).</summary>
    public async Task<TopoLayout> BuildPhysicalTopologyAsync()
    {
        var (fdb, ssids) = await GatherFdbAsync().ConfigureAwait(false);
        var gatewayIp = TraceRoute.DefaultGateway();
        var traces = await GatherTracesAsync().ConfigureAwait(false);
        return PhysicalTopology.Build(TopoInputs(), fdb, gatewayIp, ssids, traces);
    }

    private List<TopoInputDevice> TopoInputs() =>
        RawDevices().Select(d => new TopoInputDevice(
            WebId(d), d.Host, d.MacAddress, Display(d), d.Host,
            Kind(d) is DeviceKind.Switch or DeviceKind.Router or DeviceKind.AccessPoint or DeviceKind.Firewall
                || IsMikroTik(d))).ToList();

    private async Task<(Dictionary<string, IReadOnlyDictionary<string, string>> Fdb,
                        Dictionary<(string, string), string> Ssids)> GatherFdbAsync()
    {
        string community;
        lock (_lock) community = _appData.SnmpCommunity?.Trim() is { Length: > 0 } c ? c : "public";
        var bridges = RawDevices().Where(d =>
            Kind(d) is DeviceKind.Switch or DeviceKind.Router or DeviceKind.AccessPoint or DeviceKind.Firewall
            || IsMikroTik(d)).ToList();

        var fdb = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var ssids = new Dictionary<(string, string), string>();
        foreach (var d in bridges)
        {
            var (table, wifi) = await OneFdbAsync(d, community).ConfigureAwait(false);
            if (table is { Count: > 0 }) fdb[WebId(d)] = table;
            if (wifi is not null) foreach (var (iface, ssid) in wifi) ssids[(WebId(d), iface)] = ssid;
        }
        return (fdb, ssids);
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> GatherTracesAsync()
    {
        var ips = RawDevices().Select(d => d.Host).Where(h => h.Length > 0).Distinct().ToList();
        var result = new Dictionary<string, IReadOnlyList<string>>();
        using var gate = new SemaphoreSlim(24);
        var tasks = ips.Select(async ip =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { var hops = await TraceRoute.TraceAsync(ip).ConfigureAwait(false); if (hops is { Count: > 0 }) lock (result) result[ip] = hops; }
            catch { /* trace failed */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private static async Task<(Dictionary<string, string>? Fdb, Dictionary<string, string>? Ssids)> OneFdbAsync(
        Device d, string community)
    {
        List<(string Mac, string Port)>? raw = null;
        Dictionary<string, string>? wifi = null;
        var password = d.EncryptedPassword.Length > 0 ? CredentialProtector.Unprotect(d.EncryptedPassword) : "";
        try
        {
            if (IsMikroTik(d) && password.Length > 0)
            {
                using var client = new RouterOsClient(d.Host, d.Port, d.UseHttps, d.Username, password, ignoreCertErrors: true);
                raw = await client.GetBridgeHostsAsync().ConfigureAwait(false);
                try { var neigh = await client.GetNeighborsAsync().ConfigureAwait(false); raw.AddRange(neigh.Select(n => (n.Mac, n.Interface))); }
                catch { /* older RouterOS */ }
                try { wifi = await client.GetWifiSsidsAsync().ConfigureAwait(false); }
                catch { /* no wifi package */ }
            }
            else if (IsZyxelSwitch(d) && password.Length > 0)
            {
                var zy = await ZyxelSsh.GetFdbAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                raw = zy?.Select(kv => (kv.Key, kv.Value)).ToList();
            }
        }
        catch { raw = null; }

        if (raw is null || raw.Count == 0)
        {
            try { var snmp = await SnmpFdb.ReadAsync(d.Host, community).ConfigureAwait(false); raw = snmp?.Select(kv => (kv.Key, kv.Value)).ToList(); }
            catch { /* SNMP off */ }
        }
        if (raw is null) return (null, wifi is { Count: > 0 } ? wifi : null);

        var map = new Dictionary<string, string>();
        foreach (var (mac, port) in raw)
        {
            var key = PhysicalTopology.NormalizeMac(mac);
            if (key.Length == 12 && port.Length > 0 && !map.ContainsKey(key)) map[key] = port;
        }
        return (map.Count > 0 ? map : null, wifi is { Count: > 0 } ? wifi : null);
    }

    // ---- reachability ----------------------------------------------------------------------------

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
            bool up = port > 0 && await Reachability.TcpProbeAsync(d.Host, port).ConfigureAwait(false);
            lock (_lock) _online[WebId(d)] = up;
        }
        Changed?.Invoke();
    }

    private void Persist()
    {
        if (!_appData.PersistDeviceList) return;
        _appData.Devices = _devices.ToList();
        try { DeviceStore.Save(_appData); } catch { /* best effort */ }
    }

    // ---- classification helpers (public: the host's topology/backup gates share them) ------------

    public static string WebId(Device d) => d.MacAddress.Length > 0 ? d.MacAddress : d.Host;
    public static string Display(Device d) => d.Name.Length > 0 ? d.Name : d.Host;

    public static string Vendor(Device d)
    {
        if (d.ExtraInfo.TryGetValue("Hersteller (Web)", out var w) && w.Length > 0) return w;
        var oui = OuiLookup.Lookup(d.MacAddress);
        var lower = oui.ToLowerInvariant();
        if (lower.Contains("mikrotik") || lower.Contains("routerboard")) return "MikroTik";
        return oui;
    }

    public static string Model(Device d) => d.ExtraInfo.TryGetValue("Modell", out var m) ? m : "";
    public static bool IsMikroTik(Device d) => Vendor(d) == "MikroTik";
    public static bool IsZyxelSwitch(Device d) =>
        Vendor(d).Contains("Zyxel", StringComparison.OrdinalIgnoreCase) && Kind(d) == DeviceKind.Switch;

    public static DeviceKind Kind(Device d)
    {
        if (d.ExtraInfo.TryGetValue("mDNS-Modell", out var mm) && mm.Length > 0
            && DeviceClassifier.MdnsModelKind(mm) is var mk && mk != DeviceKind.Unknown)
            return mk;
        if (Vendor(d) == "MikroTik" && Model(d) is { Length: > 0 } board)
            return DeviceClassifier.MikroTikKind(board);
        return DeviceClassifier.Guess(Vendor(d), d.OpenPorts, ClassifyText(d), d.Name);
    }

    private static string ClassifyText(Device d)
    {
        var title = d.ExtraInfo.TryGetValue("Web-Titel", out var t) ? t : "";
        return (Model(d) + " " + title).Trim();
    }

    public static int VncPort(Device d) => d.OpenPorts.Contains(5900) ? 5900 : d.OpenPorts.Contains(5901) ? 5901 : 0;

    /// <summary>English kind labels. Every <see cref="DeviceKind"/> needs a case – a missing one shows a
    /// blank type for a correctly classified device.</summary>
    public static string KindText(DeviceKind k) => k switch
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
        DeviceKind.Franking => "Franking Machine",
        DeviceKind.Management => "Management (BMC)",
        DeviceKind.Smartphone => "Smartphone",
        DeviceKind.Audio => "Speaker",
        DeviceKind.GameConsole => "Game Console",
        DeviceKind.Tv => "TV",
        DeviceKind.StreamingBox => "Streaming Box",
        _ => "",
    };

    // ---- internals -------------------------------------------------------------------------------

    private Device? Find(string id) => _devices.FirstOrDefault(d => WebId(d) == id);

    private string StatusText(Device d) =>
        _online.TryGetValue(WebId(d), out var up) ? (up ? "Online" : "Offline") : "Unknown";

    private DeviceSnapshot ToSnapshot(Device d) => new(
        WebId(d), Display(d), d.Host, d.MacAddress, Vendor(d), Kind(d), KindText(Kind(d)), Model(d),
        StatusText(d), Kind(d) is DeviceKind.Router or DeviceKind.Firewall, d.EncryptedPassword.Length > 0,
        VncPort(d), d.Username, d.ExtraInfo.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList());

    private static string VendorHint(Device d)
    {
        var model = d.ExtraInfo.TryGetValue("Modell", out var m) ? m : "";
        var title = d.ExtraInfo.TryGetValue("Web-Titel", out var t) ? t : "";
        return $"{Vendor(d)} {model} {title}".ToLowerInvariant();
    }

    private static async Task<T> Safe<T>(Func<Task<T>> run, T fallback)
    {
        try { return await run().ConfigureAwait(false); } catch { return fallback; }
    }

    private static void ApplyMdns(Device d, MdnsScanner.MdnsInfo m)
    {
        if (m.HostName.Length > 0 && d.Name.Length == 0) d.Name = m.HostName;
        if (m.Model.Length > 0) d.ExtraInfo["mDNS-Modell"] = m.Model;
    }

    private static void ApplySsdp(Device d, SsdpScanner.SsdpInfo s)
    {
        if (s.FriendlyName.Length > 0 && !LooksLikeUuid(s.FriendlyName) && d.Name.Length == 0) d.Name = s.FriendlyName;
        if (s.Manufacturer.Length > 0 && !d.ExtraInfo.ContainsKey("Hersteller (Web)")) d.ExtraInfo["Hersteller (Web)"] = s.Manufacturer;
        if (s.ModelName.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = s.ModelName;
    }

    private static bool LooksLikeUuid(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase)) return true;
        int hex = t.Count(Uri.IsHexDigit), dash = t.Count(c => c == '-');
        return t.Length >= 32 && dash >= 4 && hex + dash >= t.Length - 2;
    }

    /// <summary>Scan targets = every local IPv4 network as a CIDR from its <b>real</b> subnet mask, via
    /// <see cref="NetworkInfo.GetLocalSubnets"/>. ⚠️ Not a hardcoded /24: on a /21 (or /22 …) a /24 sweep
    /// misses seven eighths of the network – which is exactly why the scan found only a fraction.</summary>
    private static string LocalScanTargets() =>
        string.Join(",", NetworkInfo.GetLocalSubnets().Select(s => s.Cidr).Distinct());

    private static int CountTargets(string targets) => SubnetScanner.CountHosts(targets);
}
