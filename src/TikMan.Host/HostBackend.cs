using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Fleet;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using TikMan.Web;

namespace TikMan.Host;

/// <summary>The headless <see cref="IWebBackend"/>: a thin adapter over the shared <see cref="FleetService"/>
/// (device list, scan, enrich, classify, reachability – all UI-free in Core) plus the web-specific pieces
/// the dashboard needs (backup streaming, SSH/VNC endpoints, the topology graph). Passwords are used only
/// at send time and stored solely via <see cref="CredentialProtector"/> – never logged.</summary>
public sealed class HostBackend : IWebBackend
{
    private readonly FleetService _fleet;
    private readonly AppData _appData;

    public HostBackend()
    {
        _appData = DeviceStore.Load();
        _fleet = new FleetService(_appData);
    }

    public string AppTitle => "TikMan";
    public string AppVersion => typeof(HostBackend).Assembly.GetName().Version?.ToString() ?? "0.0";

    // ---- device list (delegated to the fleet) ---------------------------------------------------

    public IReadOnlyList<DeviceDto> GetDevices() =>
        _fleet.Snapshot().Select(s => new DeviceDto(
            s.Id, s.Name, s.Ip, s.Mac, s.Vendor, s.KindText, s.Model, s.Status, s.IsGateway, s.HasLogin)).ToList();

    public DeviceDetail? GetDevice(string id)
    {
        var s = _fleet.SnapshotOf(id);
        if (s is null) return null;
        return new DeviceDetail(s.Id, s.Name, s.Ip, s.Mac, s.Vendor, s.KindText, s.Model, s.Status,
            s.HasLogin, s.User, s.Mac.Length > 0, s.VncPort, new List<string>(),
            s.Info.Select(kv => new KeyVal(kv.Key, kv.Value)).ToList());
    }

    public ActionResult Wake(string id)
    {
        var mac = _fleet.RawDevice(id)?.MacAddress ?? "";
        if (mac.Length == 0) return new ActionResult(false, "no MAC for this device");
        var ok = WakeOnLan.Send(mac);
        return new ActionResult(ok, ok ? $"magic packet sent to {mac}" : "send failed");
    }

    public ActionResult SetLogin(string id, string user, string password) =>
        _fleet.SetLogin(id, user, password)
            ? new ActionResult(true, "credentials updated")
            : new ActionResult(false, "device gone");

    public WebStatus GetStatus()
    {
        var st = _fleet.Status;
        return new WebStatus(st.Scanning, st.Progress, st.Phase, st.Count);
    }

    public void StartScan() => _fleet.StartScan();

    // ---- backup ---------------------------------------------------------------------------------

    public async Task<BackupResult> MakeBackupAsync(string id, bool full)
    {
        var d = _fleet.RawDevice(id);
        if (d is null) return BackupResult.Fail("device gone");
        if (d.EncryptedPassword.Length == 0) return BackupResult.Fail("no stored login");
        var password = CredentialProtector.Unprotect(d.EncryptedPassword);
        if (password.Length == 0) return BackupResult.Fail("no stored login");

        try
        {
            if (full)
            {
                if (!FleetService.IsMikroTik(d)) return BackupResult.Fail("binary backup is MikroTik-only");
                var tmp = Path.Combine(Path.GetTempPath(), "tikman-" + Guid.NewGuid().ToString("N") + ".backup");
                try
                {
                    await BackupService.DownloadFullBackupAsync(d, password, BackupMethod.Auto, d.SshPort, tmp);
                    var bytes = await File.ReadAllBytesAsync(tmp);
                    var name = BackupNaming.SuggestFileName(d.Name, FleetService.Model(d), d.Host, DateTime.Now).Replace(".rsc", ".backup");
                    return new BackupResult(true, "", name, "application/octet-stream", bytes);
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }

            // Config export: Zyxel switch over its SSH CLI, RouterOS over /export (SSH). Never logged.
            string? config; string ext;
            if (FleetService.IsZyxelSwitch(d))
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
            var fn = BackupNaming.SuggestFileName(d.Name, FleetService.Model(d), d.Host, DateTime.Now, ext);
            return new BackupResult(true, "", fn, "text/plain; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(config));
        }
        catch (Exception ex) { return BackupResult.Fail("backup failed: " + ex.Message); }
    }

    // ---- SSH terminal / VNC ---------------------------------------------------------------------

    public async Task<ITerminalSession?> OpenSshShellAsync(string id, uint cols, uint rows)
    {
        var d = _fleet.RawDevice(id);
        if (d is null || d.EncryptedPassword.Length == 0) return null;
        var password = CredentialProtector.Unprotect(d.EncryptedPassword);
        if (password.Length == 0) return null;
        return await SshTerminalSession.ConnectAsync(d.Host, d.SshPort, d.Username, password, cols, rows);
    }

    public NetEndpoint? GetVncTarget(string id)
    {
        var d = _fleet.RawDevice(id);
        var port = d is null ? 0 : FleetService.VncPort(d);
        return port > 0 ? new NetEndpoint(d!.Host, port) : null;
    }

    // ---- topology -------------------------------------------------------------------------------

    public async Task<TopoGraph> GetTopologyAsync(bool physical)
    {
        if (!physical) return BuildLogical();

        var (fdb, ssids) = await GatherFdbAsync().ConfigureAwait(false);
        var gatewayIp = TraceRoute.DefaultGateway();
        var traces = await GatherTracesAsync().ConfigureAwait(false);

        var input = _fleet.RawDevices().Select(d => new TopoInputDevice(
            FleetService.WebId(d), d.Host, d.MacAddress, FleetService.Display(d), d.Host,
            FleetService.Kind(d) is DeviceKind.Switch or DeviceKind.Router or DeviceKind.AccessPoint or DeviceKind.Firewall
                || FleetService.IsMikroTik(d))).ToList();

        var layout = PhysicalTopology.Build(input, fdb, gatewayIp, ssids, traces);
        return new TopoGraph(
            layout.Nodes.Select(n => new TopoNodeDto(n.Key, n.DeviceId, n.Title, n.Detail, n.Mac,
                n.X, n.Y, n.W, n.H, n.Fill, n.Line, n.Text)).ToList(),
            layout.Edges.Select(e => new TopoEdgeDto(e.From, e.To)).ToList());
    }

    /// <summary>The flat logical view: Internet → devices (infrastructure warm, clients green). Instant,
    /// no forwarding tables needed.</summary>
    private TopoGraph BuildLogical()
    {
        var nodes = new List<TopoNodeDto>
        {
            new("internet", "", "Internet", "", "", 40, 20, 150, 46, "#EEF1F3", "#B8C4CB", "#546E7A"),
        };
        var edges = new List<TopoEdgeDto>();
        int i = 0;
        foreach (var d in _fleet.RawDevices())
        {
            var key = "d" + i;
            double x = 40 + i % 8 * 190.0, y = 120 + i / 8 * 90.0;
            var gw = FleetService.Kind(d) is DeviceKind.Router or DeviceKind.Firewall;
            nodes.Add(new TopoNodeDto(key, FleetService.WebId(d), FleetService.Display(d), d.Host, d.MacAddress,
                x, y, 168, 56, gw ? "#FFF1DF" : "#EDF8ED", gw ? "#F3C48A" : "#AFD8AF", gw ? "#B26B12" : "#3F7A46"));
            edges.Add(new TopoEdgeDto("internet", key));
            i++;
        }
        return new TopoGraph(nodes, edges);
    }

    /// <summary>Collects each infrastructure device's MAC→port forwarding table: RouterOS over REST (login,
    /// + neighbour table + WLAN SSIDs), a Zyxel switch over its SSH CLI (login), otherwise SNMP (no login).
    /// Best-effort per device. Returns the FDB and the (bridge, port)→SSID labels.</summary>
    private async Task<(Dictionary<string, IReadOnlyDictionary<string, string>> Fdb,
                        Dictionary<(string, string), string> Ssids)> GatherFdbAsync()
    {
        var community = _appData.SnmpCommunity?.Trim() is { Length: > 0 } c ? c : "public";
        var bridges = _fleet.RawDevices().Where(d =>
            FleetService.Kind(d) is DeviceKind.Switch or DeviceKind.Router or DeviceKind.AccessPoint or DeviceKind.Firewall
            || FleetService.IsMikroTik(d)).ToList();

        var fdb = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var ssids = new Dictionary<(string, string), string>();
        foreach (var d in bridges)
        {
            var (table, wifi) = await OneFdbAsync(d, community).ConfigureAwait(false);
            if (table is { Count: > 0 }) fdb[FleetService.WebId(d)] = table;
            if (wifi is not null)
                foreach (var (iface, ssid) in wifi) ssids[(FleetService.WebId(d), iface)] = ssid;
        }
        return (fdb, ssids);
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> GatherTracesAsync()
    {
        var ips = _fleet.RawDevices().Select(d => d.Host).Where(h => h.Length > 0).Distinct().ToList();
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
            if (FleetService.IsMikroTik(d) && password.Length > 0)
            {
                using var client = new RouterOsClient(d.Host, d.Port, d.UseHttps, d.Username, password, ignoreCertErrors: true);
                raw = await client.GetBridgeHostsAsync().ConfigureAwait(false);
                try { var neigh = await client.GetNeighborsAsync().ConfigureAwait(false); raw.AddRange(neigh.Select(n => (n.Mac, n.Interface))); }
                catch { /* no neighbour endpoint */ }
                try { wifi = await client.GetWifiSsidsAsync().ConfigureAwait(false); }
                catch { /* no wifi package */ }
            }
            else if (FleetService.IsZyxelSwitch(d) && password.Length > 0)
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
}
