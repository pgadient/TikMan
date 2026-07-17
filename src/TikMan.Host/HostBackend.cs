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
            await EnrichAsync(CancellationToken.None).ConfigureAwait(false); // name devices the port scan can't
            await ProbeAllAsync().ConfigureAwait(false);   // fresh status right away, not up to 30s later
        }
        catch { /* a scan that fails leaves the last-known list in place */ }
        finally { lock (_lock) { _scanning = false; _phase = ""; _progress = 0; } }
    }

    /// <summary>Merges a freshly discovered host into the list, matched by MAC (or IP when it has no
    /// MAC), so a re-scan updates rather than duplicates. A record carrying a board (or a Winbox/API
    /// port) is a MikroTik – MNDP only ever answers on MikroTik gear – so the vendor is pinned here,
    /// the same signal the GUI's <c>IdentifiedVendor</c> trusts without a login.</summary>
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
            d.ExtraInfo["Hersteller (Web)"] = "MikroTik"; // Winbox/API port ⇒ MikroTik, even without a board
        if (f.Version.Length > 0) d.ExtraInfo["Version"] = f.Version;
        // Port scan gives the fuller port set; a discovery-only record (MNDP) has none, so don't wipe.
        if (f.OpenPorts.Count > 0) d.OpenPorts = f.OpenPorts.Distinct().OrderBy(p => p).ToList();
        if (existing is null) _devices.Add(d);
    }

    /// <summary>Names devices the TCP port scan can't: MikroTik announce their board over MNDP (no
    /// login), and Apple/Sonos/smart-TV gear sits on generic ODM OUIs behind a bare web port but names
    /// itself over mDNS/SSDP. All three run without credentials and on any network. Best-effort – a
    /// blocked multicast just means fewer names, never a failure.</summary>
    private async Task EnrichAsync(CancellationToken ct)
    {
        var mndpTask = Safe(() => MndpScanner.DiscoverAsync(TimeSpan.FromSeconds(4), null, ct),
            new List<DiscoveredDevice>());
        var mdnsTask = Safe(() => MdnsScanner.DiscoverAsync(TimeSpan.FromSeconds(4), ct),
            new Dictionary<string, MdnsScanner.MdnsInfo>());
        var ssdpTask = Safe(() => SsdpScanner.DiscoverAsync(TimeSpan.FromSeconds(4), ct),
            new Dictionary<string, SsdpScanner.SsdpInfo>());
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

        await ProbeDevicesAsync(community, ct).ConfigureAwait(false); // per-device HTTP/SNMP for model+vendor
    }

    /// <summary>Per-device, unauthenticated probes that fill the model and vendor a bare port scan can't:
    /// the web UI's &lt;title&gt; (often the exact model) + a brand from it, and SNMP's sysDescr (the exact
    /// model on printers/copiers/switches). Runs concurrently, gently capped. Only fills gaps – a name or
    /// model an earlier, stronger source set is never overwritten.</summary>
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

        // QNAP first: its plain web pages return noise, so ask the QTS login endpoint. If it answers,
        // the device is a QNAP NAS and the other web probes would only muddy it.
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

        // Web fingerprint: server header, page <title> (often the exact model) and a brand from it.
        if (web)
        {
            var fp = await HttpFingerprint.ProbeAsync(host, ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (fp.WebServer.Length > 0 && !d.ExtraInfo.ContainsKey("Webserver")) d.ExtraInfo["Webserver"] = fp.WebServer;
                if (fp.Title.Length > 0 && !d.ExtraInfo.ContainsKey("Web-Titel")) d.ExtraInfo["Web-Titel"] = fp.Title;
                if (fp.Vendor.Length > 0 && !d.ExtraInfo.ContainsKey("Hersteller (Web)"))
                    d.ExtraInfo["Hersteller (Web)"] = fp.Vendor;
            }
        }

        // TLS cert (443): some devices embed model/serial/MAC in the subject and speak only legacy TLS
        // no HTTP page loads over (e.g. a Cisco SPA122 ATA). After the web probe, so a clean title wins.
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

        // Vendor-gated web probes: a Brother printer, a Swisscom Internet-Box, a Frontier-Silicon radio
        // all expose exact model/serial/firmware on their own endpoints. Gate on the hints gathered so far.
        var hint = VendorHint(d);
        if (web && hint.Contains("brother") &&
            await BrotherProbe.QueryAsync(host, ct).ConfigureAwait(false) is { } br)
        {
            lock (_lock)
            {
                if (br.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = br.Serial;
                if (br.MainFirmware.Length > 0) d.ExtraInfo["Firmware"] = br.MainFirmware;
                foreach (var kv in br.SubFirmware) d.ExtraInfo[kv.Key] = kv.Value;
            }
        }
        if (web && (hint.Contains("swisscom") || hint.Replace("-", "").Replace(" ", "").Contains("internetbox")) &&
            await SwisscomProbe.QueryAsync(host, ct).ConfigureAwait(false) is { } box)
        {
            lock (_lock)
            {
                if (box.ModelName.Length > 0) d.ExtraInfo["Modell"] = box.ModelName;
                if (box.Serial.Length > 0) d.SerialNumber = box.Serial;
                if (box.Firmware.Length > 0) d.ExtraInfo["Firmware"] = box.Firmware;
            }
        }
        if (web && (hint.Contains("frontier") || hint.Contains("internet radio")) &&
            await FrontierSiliconProbe.QueryAsync(host, ct).ConfigureAwait(false) is { } radio)
        {
            lock (_lock)
            {
                if (radio.Name.Length > 0) d.ExtraInfo["Modell"] = radio.Name;
                if (radio.Vendor.Length > 0 && !d.ExtraInfo.ContainsKey("Hersteller (Web)")) d.ExtraInfo["Hersteller (Web)"] = radio.Vendor;
                if (radio.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = radio.Serial;
                if (radio.Firmware.Length > 0) d.ExtraInfo["Firmware"] = radio.Firmware;
            }
        }

        // SNMP sysDescr/sysName: the exact model on printers/copiers/switches, no login.
        if (await SnmpProbe.QueryAsync(host, ct, community).ConfigureAwait(false) is { } s)
        {
            lock (_lock)
            {
                if (s.SysName.Length > 0 && d.Name.Length == 0) d.Name = s.SysName;
                if (s.SysDescr.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = s.SysDescr;
            }
        }

        // Generic vendor SSH probe (Zyxel/Netgear/D-Link/HPE/… ): model/serial/firmware from read-only
        // "show" commands, ordered by the vendor hint. Needs a login.
        if (ports.Contains(22) && d.EncryptedPassword.Length > 0 && d.Username.Trim().Length > 0)
        {
            var password = CredentialProtector.Unprotect(d.EncryptedPassword);
            if (password.Length > 0 &&
                await SshInfoProbe.QueryAsync(host, d.SshPort, d.Username.Trim(), password, hint, ct)
                    .ConfigureAwait(false) is { } ssh)
            {
                lock (_lock)
                {
                    if (ssh.Model.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = ssh.Model;
                    if (ssh.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = ssh.Serial;
                    if (ssh.Firmware.Length > 0 && !d.ExtraInfo.ContainsKey("Firmware")) d.ExtraInfo["Firmware"] = ssh.Firmware;
                }
            }
        }

        // DNS (UDP 53) isn't seen by the TCP scan; a UDP forwarder (Swisscom box, dnsmasq) gets its badge
        // and the open port, which the classifier weighs.
        try
        {
            if (await DnsProbe.IsOpenAsync(host, ct).ConfigureAwait(false))
                lock (_lock) { if (!d.OpenPorts.Contains(53)) d.OpenPorts.Add(53); }
        }
        catch { /* best effort */ }
    }

    /// <summary>Every vendor signal held for a device, one lowercase haystack: the OUI/probe vendor, the
    /// model and the scraped web title – what the vendor-gated probes match against.</summary>
    private static string VendorHint(Device d)
    {
        var vendor = Vendor(d);
        var model = d.ExtraInfo.TryGetValue("Modell", out var m) ? m : "";
        var title = d.ExtraInfo.TryGetValue("Web-Titel", out var t) ? t : "";
        return $"{vendor} {model} {title}".ToLowerInvariant();
    }

    private static async Task<T> Safe<T>(Func<Task<T>> run, T fallback)
    {
        try { return await run().ConfigureAwait(false); } catch { return fallback; }
    }

    private static void ApplyMdns(Device d, MdnsScanner.MdnsInfo m)
    {
        if (m.HostName.Length > 0 && d.Name.Length == 0) d.Name = m.HostName;
        if (m.Model.Length > 0) d.ExtraInfo["mDNS-Modell"] = m.Model; // raw model (e.g. "AudioAccessory5,1")
    }

    private static void ApplySsdp(Device d, SsdpScanner.SsdpInfo s)
    {
        // The friendly name is the owner-given one – the best label there is – unless it's just a UUID.
        if (s.FriendlyName.Length > 0 && !LooksLikeUuid(s.FriendlyName) && d.Name.Length == 0)
            d.Name = s.FriendlyName;
        if (s.Manufacturer.Length > 0 && !d.ExtraInfo.ContainsKey("Hersteller (Web)"))
            d.ExtraInfo["Hersteller (Web)"] = s.Manufacturer;
        if (s.ModelName.Length > 0 && !d.ExtraInfo.ContainsKey("Modell"))
            d.ExtraInfo["Modell"] = s.ModelName;
    }

    /// <summary>A bare UUID (e.g. "uuid:2f402f80-…" or a hex/dashed blob) is a worse label than none –
    /// SSDP responders often use one as the friendly name. Filter it so it doesn't become the device name.</summary>
    private static bool LooksLikeUuid(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase)) return true;
        int hex = t.Count(Uri.IsHexDigit), dash = t.Count(c => c == '-');
        return t.Length >= 32 && dash >= 4 && hex + dash >= t.Length - 2;
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

    public async Task<TopoGraph> GetTopologyAsync(bool physical)
    {
        if (!physical) return BuildLogical();

        // Physical: gather the bridge forwarding tables (who sees which MAC on which port) plus the WLAN
        // SSIDs and traced routes, then let the shared Core builder place everything. Slow – it talks to
        // every infrastructure device – but only when the physical tab is opened.
        var (fdb, ssids) = await GatherFdbAsync().ConfigureAwait(false);
        var gatewayIp = TraceRoute.DefaultGateway();
        var traces = await GatherTracesAsync().ConfigureAwait(false);

        List<TopoInputDevice> input;
        lock (_lock)
            input = _devices.Select(d => new TopoInputDevice(
                WebId(d), d.Host, d.MacAddress, Display(d), d.Host,
                Kind(d) is DeviceKind.Switch or DeviceKind.Router or DeviceKind.AccessPoint or DeviceKind.Firewall
                    || IsMikroTik(d))).ToList();

        var layout = PhysicalTopology.Build(input, fdb, gatewayIp, ssids, traces);
        return new TopoGraph(
            layout.Nodes.Select(n => new TopoNodeDto(n.Key, n.DeviceId, n.Title, n.Detail, n.Mac,
                n.X, n.Y, n.W, n.H, n.Fill, n.Line, n.Text)).ToList(),
            layout.Edges.Select(e => new TopoEdgeDto(e.From, e.To)).ToList());
    }

    /// <summary>The flat logical view: Internet → devices (infrastructure warm, clients green). No
    /// forwarding tables needed, so it is instant and works on any network.</summary>
    private TopoGraph BuildLogical()
    {
        lock (_lock)
        {
            var nodes = new List<TopoNodeDto>
            {
                new("internet", "", "Internet", "", "", 40, 20, 150, 46, "#EEF1F3", "#B8C4CB", "#546E7A"),
            };
            var edges = new List<TopoEdgeDto>();
            int i = 0;
            foreach (var d in _devices)
            {
                var key = "d" + i;
                double x = 40 + i % 8 * 190.0, y = 120 + i / 8 * 90.0;
                var gw = Kind(d) is DeviceKind.Router or DeviceKind.Firewall;
                nodes.Add(new TopoNodeDto(key, WebId(d), Display(d), d.Host, d.MacAddress,
                    x, y, 168, 56, gw ? "#FFF1DF" : "#EDF8ED", gw ? "#F3C48A" : "#AFD8AF",
                    gw ? "#B26B12" : "#3F7A46"));
                edges.Add(new TopoEdgeDto("internet", key));
                i++;
            }
            return new TopoGraph(nodes, edges);
        }
    }

    /// <summary>Collects each infrastructure device's MAC→port forwarding table: RouterOS over REST (with
    /// a login, plus its neighbour table and WLAN SSIDs), a Zyxel switch over its SSH CLI (with a login),
    /// otherwise SNMP (no login, vendor-neutral). Best-effort per device – one that won't answer simply
    /// contributes nothing. Returns the FDB and the (bridge, port)→SSID labels for the wlan ports.</summary>
    private async Task<(Dictionary<string, IReadOnlyDictionary<string, string>> Fdb,
                        Dictionary<(string, string), string> Ssids)> GatherFdbAsync()
    {
        List<Device> bridges;
        string community;
        lock (_lock)
        {
            community = _appData.SnmpCommunity?.Trim() is { Length: > 0 } c ? c : "public";
            bridges = _devices.Where(d =>
                Kind(d) is DeviceKind.Switch or DeviceKind.Router or DeviceKind.AccessPoint or DeviceKind.Firewall
                || IsMikroTik(d)).ToList();
        }

        var fdb = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var ssids = new Dictionary<(string, string), string>();
        foreach (var d in bridges)
        {
            var (table, wifi) = await OneFdbAsync(d, community).ConfigureAwait(false);
            if (table is { Count: > 0 }) fdb[WebId(d)] = table;
            if (wifi is not null)
                foreach (var (iface, ssid) in wifi) ssids[(WebId(d), iface)] = ssid;
        }
        return (fdb, ssids);
    }

    /// <summary>Traceroutes every device in parallel (capped), so routed segments (devices behind another
    /// router) can be chained on the map. Best-effort: unprivileged ICMP may not resolve hops, in which
    /// case a device without an FDB sighting just lands under "unknown path".</summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> GatherTracesAsync()
    {
        List<string> ips;
        lock (_lock) ips = _devices.Select(d => d.Host).Where(h => h.Length > 0).Distinct().ToList();
        var result = new Dictionary<string, IReadOnlyList<string>>();
        using var gate = new SemaphoreSlim(24);
        var tasks = ips.Select(async ip =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var hops = await TraceRoute.TraceAsync(ip).ConfigureAwait(false);
                if (hops is { Count: > 0 }) lock (result) result[ip] = hops;
            }
            catch { /* trace failed – device falls back to FDB/unknown */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private async Task<(Dictionary<string, string>? Fdb, Dictionary<string, string>? Ssids)> OneFdbAsync(
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
                // Neighbour table: extra MAC→physical-port sightings, merged into the FDB below.
                try
                {
                    var neigh = await client.GetNeighborsAsync().ConfigureAwait(false);
                    raw.AddRange(neigh.Select(n => (n.Mac, n.Interface)));
                }
                catch { /* no neighbour endpoint / older RouterOS */ }
                // WLAN SSIDs label the wlan ports ("wifi1 (MyWLAN)").
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

        // No login (or the authenticated read failed) → SNMP, which is vendor-neutral and needs none.
        if (raw is null || raw.Count == 0)
        {
            try
            {
                var snmp = await SnmpFdb.ReadAsync(d.Host, community).ConfigureAwait(false);
                raw = snmp?.Select(kv => (kv.Key, kv.Value)).ToList();
            }
            catch { /* SNMP off – nothing to add */ }
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

    // ---- mapping helpers ------------------------------------------------------------------------

    private Device? Find(string id) =>
        _devices.FirstOrDefault(d => WebId(d) == id);

    private static string WebId(Device d) => d.MacAddress.Length > 0 ? d.MacAddress : d.Host;
    private static string Display(Device d) => d.Name.Length > 0 ? d.Name : d.Host;

    private static string Vendor(Device d)
    {
        // Probe-derived vendor first (MNDP/mDNS/SSDP wrote it here); the MAC OUI is the last resort, the
        // same order the GUI's IdentifiedVendor uses so local and VPN scans agree.
        if (d.ExtraInfo.TryGetValue("Hersteller (Web)", out var w) && w.Length > 0) return w;
        var oui = OuiLookup.Lookup(d.MacAddress);
        var lower = oui.ToLowerInvariant();
        if (lower.Contains("mikrotik") || lower.Contains("routerboard")) return "MikroTik";
        return oui;
    }

    private static string Model(Device d) =>
        d.ExtraInfo.TryGetValue("Modell", out var m) ? m : "";

    private static string Board(Device d) =>
        d.ExtraInfo.TryGetValue("Modell", out var m) ? m : "";

    private static bool IsMikroTik(Device d) => Vendor(d) == "MikroTik";

    private static bool IsZyxelSwitch(Device d) =>
        Vendor(d).Contains("Zyxel", StringComparison.OrdinalIgnoreCase) && Kind(d) == DeviceKind.Switch;

    /// <summary>Device kind, mirroring the GUI's order of trust. A MikroTik is split by its board code
    /// (CRS/CSS ⇒ switch, a board with a radio ⇒ AP, else router) – the generic model rules don't know
    /// those codes – otherwise the shared classifier decides from vendor/ports/model.</summary>
    private static DeviceKind Kind(Device d)
    {
        // The mDNS model is identity and outranks all – an Apple HomePod/Apple TV names itself, where
        // one shared OUI and no open ports would otherwise tell us nothing.
        if (d.ExtraInfo.TryGetValue("mDNS-Modell", out var mm) && mm.Length > 0
            && DeviceClassifier.MdnsModelKind(mm) is var mk && mk != DeviceKind.Unknown)
            return mk;
        if (Vendor(d) == "MikroTik" && Model(d) is { Length: > 0 } board)
            return DeviceClassifier.MikroTikKind(board);
        return DeviceClassifier.Guess(Vendor(d), d.OpenPorts, ClassifyText(d), d.Name);
    }

    /// <summary>The model text the classifier weighs: the SNMP/MNDP model plus the web title, since the
    /// exact model shows up in one or the other (a printer's title says the model, a switch's sysDescr
    /// does). The DTO's model column stays the clean <see cref="Model"/> value.</summary>
    private static string ClassifyText(Device d)
    {
        var model = Model(d);
        var title = d.ExtraInfo.TryGetValue("Web-Titel", out var t) ? t : "";
        return (model + " " + title).Trim();
    }

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
    /// in the WPF layer; duplicating a short map here keeps Core free of a display concern.
    /// ⚠️ Every <see cref="DeviceKind"/> needs a case – a missing one falls to "" and a correctly
    /// classified device shows a blank type (that bug ate the Apple HomePod's "Audio").</summary>
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
        DeviceKind.Franking => "Franking Machine",
        DeviceKind.Management => "Management (BMC)",
        DeviceKind.Smartphone => "Smartphone",
        DeviceKind.Audio => "Speaker",
        DeviceKind.GameConsole => "Game Console",
        DeviceKind.Tv => "TV",
        DeviceKind.StreamingBox => "Streaming Box",
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
