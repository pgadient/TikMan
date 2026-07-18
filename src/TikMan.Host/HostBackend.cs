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
        var layout = physical ? await _fleet.BuildPhysicalTopologyAsync().ConfigureAwait(false)
                              : _fleet.BuildLogicalTopology();
        return new TopoGraph(
            layout.Nodes.Select(n => new TopoNodeDto(n.Key, n.DeviceId, n.Title, n.Detail, n.Mac,
                n.X, n.Y, n.W, n.H, n.Fill, n.Line, n.Text)).ToList(),
            layout.Edges.Select(e => new TopoEdgeDto(e.From, e.To)).ToList());
    }
}
