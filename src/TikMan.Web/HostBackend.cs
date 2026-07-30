using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Fleet;
using TikMan.Core.Models;
using TikMan.Core.Storage;

namespace TikMan.Web;

/// <summary>The UI-free <see cref="IWebBackend"/>: a thin adapter over the shared <see cref="FleetService"/>
/// (device list, scan, enrich, classify, reachability – all UI-free in Core) plus the web-specific pieces
/// the dashboard needs (backup streaming, SSH/VNC endpoints, the topology graph). Passwords are used only
/// at send time and stored solely via <see cref="CredentialProtector"/> – never logged.
/// <para>Lives here (not in TikMan.Host) so the headless host <b>and</b> the Avalonia GUI can both serve the
/// dashboard. The GUI passes its own live fleet, so the web view mirrors what is on screen instead of
/// scanning a second time – the same "one shared inventory" the WPF client has.</para></summary>
public sealed class HostBackend : IWebBackend
{
    private readonly FleetService _fleet;
    private readonly AppData _appData;

    /// <summary>Headless use: loads the settings and owns its own fleet.</summary>
    public HostBackend() : this(null, null) { }

    /// <summary>Shares an existing fleet/settings (the GUI case); null falls back to loading its own.</summary>
    public HostBackend(FleetService? fleet, AppData? appData)
    {
        _appData = appData ?? DeviceStore.Load();
        _fleet = fleet ?? new FleetService(_appData);
    }

    public string AppTitle => "TikMan";
    // Three components, matching the WPF title and the release tags – not Version.ToString()'s "2.2.2.0".
    // ⚠️ The ENTRY assembly (the running client), not this one. TikMan.Web is a library with no <Version>,
    // so asking it returned .NET's default 1.0.0 – which is what the dashboard proudly displayed while the
    // app was on 2.2.2. Passing no assembly makes AppVersion.Current use the entry assembly.
    public string AppVersion => TikMan.Core.AppVersion.Text();

    // ---- device list (delegated to the fleet) ---------------------------------------------------

    public IReadOnlyList<DeviceDto> GetDevices() =>
        _fleet.Snapshot().Select(ToDto).ToList();

    private static DeviceDto ToDto(DeviceSnapshot s) => new(
        s.Id, s.Name, s.Ip, s.Mac, s.Vendor, s.KindText, s.Model, s.Status, s.IsGateway, s.HasLogin,
        s.MacVendor, s.Ipv6Summary, s.Serial, s.Os, s.Firmware,
        s.Cpu, s.Memory, s.Uptime, s.LatestVersion, s.UpdateAvailable,
        s.Badges.Select(b => new BadgeDto(b.Name, b.Url, b.Colour, b.Tooltip)).ToList());

    /// <summary>One row per IPv6 address, mirroring the app's IPv6 tab: the device facts are repeated on
    /// every row on purpose, so each address can be read (and sorted) on its own.</summary>
    public IReadOnlyList<Ipv6RowDto> GetIpv6Rows()
    {
        var rows = new List<Ipv6RowDto>();
        var group = 0;
        foreach (var s in _fleet.Snapshot().Where(d => d.HasIpv6))
        {
            group++;
            foreach (var e in s.Ipv6Entries.OrderBy(e => e.Address, StringComparer.OrdinalIgnoreCase))
                rows.Add(new Ipv6RowDto(
                    s.Id, group, Prefer(e.Facts.Name, s.Name), s.KindText, s.Ip, e.Address, e.Tag.Text,
                    s.Mac, s.MacVendor, Prefer(e.Facts.Vendor, s.Vendor), Prefer(e.Facts.Model, s.Model),
                    s.Status,
                    // ⚠️ The badges of THIS address, not the device's IPv4 ones: a service can be bound to
                    // one address only, which is the whole reason the per-address view exists.
                    e.Badges.Select(b => new BadgeDto(b.Name, b.Url, b.Colour, b.Tooltip)).ToList(),
                    Prefer(e.Facts.Serial, s.Serial), Prefer(e.Facts.Os, s.Os),
                    e.Facts.HasShares ? string.Join(" ", e.Facts.ShareNames)
                        : e.Facts.SharesDenied ? "denied" : "",
                    s.Firmware, s.LatestVersion, s.InstalledRelease, s.UpdateRelease,
                    s.Cpu, s.Memory, s.Uptime));
        }
        return rows;
    }

    /// <summary>The per-address answer where there is one, the device fact otherwise – see
    /// <see cref="Ipv6Facts"/>.</summary>
    private static string Prefer(string perAddress, string device) =>
        perAddress.Length > 0 ? perAddress : device;

    public DeviceDetail? GetDevice(string id)
    {
        var s = _fleet.SnapshotOf(id);
        if (s is null) return null;
        return new DeviceDetail(s.Id, s.Name, s.Ip, s.Mac, s.Vendor, s.KindText, s.Model, s.Status,
            s.HasLogin, s.User, s.Mac.Length > 0, s.VncPort, s.Ipv6,
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

    public async Task<ITerminalSession?> OpenSshShellAsync(string id, uint cols, uint rows,
        string? user = null, string? password = null)
    {
        var d = _fleet.RawDevice(id);
        if (d is null) return null;

        // Credentials typed into the terminal win; the stored login is the fallback. ⚠️ Neither is written
        // anywhere here – the typed pair authenticates this session and is then gone, exactly like the
        // password a normal SSH client prompts for.
        var loginUser = user is { Length: > 0 } ? user : d.Username;
        var loginPass = password is { Length: > 0 }
            ? password
            : (d.EncryptedPassword.Length > 0 ? CredentialProtector.Unprotect(d.EncryptedPassword) : "");
        if (loginPass.Length == 0) return null;

        return await SshTerminalSession.ConnectAsync(d.Host, d.SshPort, loginUser, loginPass, cols, rows);
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
                n.X, n.Y, n.W, n.H, n.Fill, n.Line, n.Text, n.Vendor, n.Model, n.Kind)).ToList(),
            layout.Edges.Select(e => new TopoEdgeDto(e.From, e.To)).ToList());
    }
}
