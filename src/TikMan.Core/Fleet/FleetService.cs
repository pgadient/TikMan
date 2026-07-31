using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.Core.Fleet;

/// <summary>A computed, UI-free view of one device – everything a list needs, already classified. The
/// raw <see cref="Device"/> stays with <see cref="FleetService"/>; this is the read model.</summary>
/// <summary>A grid row whose detail area can be opened. Implemented by both the IPv4 device row
/// (<see cref="DeviceSnapshot"/>) and the IPv6 per-address row, so one code-behind wiring drives the
/// expander on both grids.</summary>
public interface IExpandableRow : INotifyPropertyChanged
{
    bool IsExpanded { get; set; }
    bool HasRowDetails { get; }
}

public sealed record DeviceSnapshot(
    string Id, string Name, string Ip, string Mac, string Vendor, DeviceKind Kind, string KindText,
    string Model, string Status, bool IsGateway, bool HasLogin, int VncPort, string User,
    IReadOnlyList<KeyValuePair<string, string>> Info, IReadOnlyList<string> Ipv6,
    bool CanConfigBackup, bool CanFullBackup, bool CanUpdate, bool CanReadLogs, string LoginError,
    bool CanUseCredentials,
    string Serial, string Os, string Firmware, IReadOnlyList<string> Shares, string SharesStatus,
    string ShareHost,
    IReadOnlyList<ServiceBadge> Badges,
    int SshPort, IReadOnlyList<int> OpenPorts,
    string Cpu, string Memory, string Uptime,
    string MacVendor,
    string LatestVersion, string InstalledRelease, string UpdateRelease, bool UpdateAvailable,
    string LatestVersionUrl, string VersionUrl,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Ipv6Ports,
    IReadOnlyDictionary<string, Ipv6Facts> Ipv6Meta)
    : INotifyPropertyChanged, IExpandableRow
{
    public bool HasIpv6 => Ipv6.Count > 0;
    public bool HasShares => Shares.Count > 0;
    /// <summary>True when the host refused to list its shares – worth saying, because the row otherwise
    /// looks as though TikMan never asked.</summary>
    public bool SharesDenied => SharesStatus == "denied";

    /// <summary>True when a login is stored but the last attempt to use it was rejected. Distinct from "no
    /// login": the row is configured, it just does not work – which is exactly what the user needs to see
    /// instead of wondering why nothing gets read.</summary>
    public bool LoginBroken => HasLogin && LoginError.Length > 0;

    /// <summary>Hover text for a rejected login: the headline plus what the device actually said, so the
    /// user can tell a wrong password from an unreachable host or a refused transport.</summary>
    public string LoginErrorTip => LoginError.Length > 0 ? T("Av_LoginBroken") + "\n" + LoginError : "";

    /// <summary>True when the port scan reached this device and none of the known services answered.
    /// <para>Said out loud for the same reason the IPv6 tab says it: an empty protocol cell reads as "TikMan
    /// never looked", when the truth is that it asked and the device offers nothing on the scanned ports.
    /// Devices that were never scanned at all have no open ports either, which is why this is tied to the
    /// status – a restored, unchecked entry stays silent instead of claiming a result.</para></summary>
    public bool NothingAnswered => Badges.Count == 0 && Status != "Unknown";

    /// <summary>Whether the expandable row carries anything worth showing.</summary>
    public bool HasRowDetails => HasIpv6 || HasShares || SharesDenied || Info.Count > 0;

    /// <summary>The Latest cell has a page to open but no parsed version number – rendered as a "manual
    /// search" link rather than a fabricated version (vendors whose page is client-rendered or whose model
    /// is end-of-life).</summary>
    public bool LatestIsManual => LatestVersion.Length == 0 && LatestVersionUrl.Length > 0;

    /// <summary>Clicking the Latest cell does something – it has either a parsed version or a manual-search
    /// link behind it.</summary>
    public bool HasLatestLink => LatestVersionUrl.Length > 0;

    /// <summary>The installed-version cell links to that release's notes (MikroTik's per-version changelog).</summary>
    public bool HasVersionLink => VersionUrl.Length > 0;

    /// <summary>What the Latest column shows: the parsed version, or a localised "manual search" when only a
    /// link is known, or nothing.</summary>
    public string LatestDisplay =>
        LatestVersion.Length > 0 ? LatestVersion : LatestIsManual ? T("Av_ManualSearch") : "";

    /// <summary>UI state: is this row's detail panel open? Deliberately mutable and observable (the rest of
    /// the record is an immutable read model) so a grid can bind a per-row expander to it. It is <b>not</b>
    /// part of the record's value equality, and the fleet carries it over across refreshes by device id –
    /// otherwise the 30-second refresh would snap every open row shut again.</summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Traffic-light colour for the status dot: green online, red offline, sunflower yellow for
    /// "answered a discovery, not validated since", grey never checked.</summary>
    public string StatusColour => Status switch
    {
        "Online" => "#4CAF50",
        "Offline" => "#E57373",
        "Answered" => "#FFC512",
        _ => "#9E9E9E",
    };

    /// <summary>UNC paths for the share shortcuts (\\host\share).</summary>
    public IReadOnlyList<ShareLink> ShareLinks =>
        Shares.Select(s => new ShareLink(s, $@"\\{ShareHost}\{s}")).ToList();

    /// <summary>All IPv6 addresses on one line – what a grid cell can show without wrapping the row.</summary>
    public string Ipv6Summary => string.Join("  ", Ipv6);

    /// <summary>True when the last reachability probe said the device is down. "Unknown" (never probed) is
    /// deliberately not offline – a restored list must not come up looking like the whole network is dead.
    /// <para>Drives a style class on the row; the colours themselves stay in the view, where the theme can
    /// have an opinion about them.</para></summary>
    public bool IsOffline => Status == "Offline";

    /// <summary>Scope tags for this device's IPv6 addresses ("Global", "ULA", "Link-local"), so the list
    /// shows at a glance whether an address is routable or only valid on its own segment. Most hosts carry
    /// several at once, hence a list rather than a single value.</summary>
    public IReadOnlyList<Ipv6Tag> Ipv6Tags =>
        Ipv6Scope.Summarise(Ipv6).Select(Ipv6Tag.For).ToList();

    /// <summary>Each IPv6 address with its own scope tag and the services that answered on that address –
    /// the per-address view shows which address is which, where a device-level column can only say what the
    /// device has overall.</summary>
    public IReadOnlyList<Ipv6Entry> Ipv6Entries =>
        Ipv6.Select(a =>
        {
            var probed = Ipv6Ports.TryGetValue(a, out var ports);
            var badges = probed && ports!.Count > 0
                // The address goes in brackets so the badge links are valid v6 URLs.
                ? ServiceBadges.For($"[{a}]", ports!, new Dictionary<string, string>())
                : Array.Empty<ServiceBadge>();
            var facts = Ipv6Meta.TryGetValue(a, out var f) ? f : Ipv6Facts.None;
            return new Ipv6Entry(a, Ipv6Tag.For(Ipv6Scope.Classify(a)), badges, probed, facts);
        }).ToList();
}

/// <summary>One SMB share shortcut: its name and the UNC path to open.</summary>
public sealed record ShareLink(string Name, string UncPath);

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
    // WebId → answered a discovery this session. Weaker evidence than a reachability probe (hence its own
    // set, not an _online entry): the device spoke once, but nothing has validated it since. Shown yellow.
    private readonly HashSet<string> _seenAlive = new();
    // WebId → how the last share enumeration went, so the UI can say "denied" instead of showing an empty
    // area that reads as "TikMan didn't bother".
    private readonly Dictionary<string, ShareListStatus> _shareStatus = new();
    // WebId → the host form the enumeration succeeded with (see ShareListResult.Host).
    private readonly Dictionary<string, string> _shareHost = new();

    /// <summary>IPv6 address → the ports that answered on it. Keyed by address, not by device: the whole
    /// point is that two addresses of one device can behave differently. A missing key means "not probed",
    /// which is a different statement from an empty list.</summary>
    private readonly Dictionary<string, IReadOnlyList<int>> _ipv6Ports = new();

    /// <summary>IPv6 address → what talking to <b>that address</b> revealed (name, OS, model, shares …).
    /// Keyed by address for the same reason as <see cref="_ipv6Ports"/>: a dual-stack host can answer with a
    /// different identity, or refuse entirely, depending on which address you dial.</summary>
    private readonly Dictionary<string, Ipv6Facts> _ipv6Meta = new();

    /// <summary>WebId → why the stored login did not work, or "" when it did. A missing entry means the
    /// credentials have not been tried yet – which is different from "they failed", and the UI shows only
    /// the failure.</summary>
    private readonly Dictionary<string, string> _loginFailure = new();

    /// <summary>WebId → last /system/resource read (CPU, memory, uptime). Only devices with a stored login
    /// appear here; a missing entry means "never read", not "zero".</summary>
    private readonly Dictionary<string, ResourceInfo> _resources = new();

    /// <summary>WebId → CPU/RAM readings over time, for the monitoring chart. Capped per device: this is a
    /// live view, not a metrics store, and an app left running for days must not grow without bound.</summary>
    private readonly Dictionary<string, List<ResourceSnapshot>> _history = new();

    /// <summary>How many points to keep per device. Matches the chart's plot width (HistoryChart.Capacity):
    /// a fuller buffer would only ever show the newest this-many anyway, and 50 across the width keeps each
    /// sample wide enough to read rather than a hairline. At the 30 s interval that is about 25 minutes.</summary>
    private const int HistoryPoints = 50;

    /// <summary>WebId → the last update check, plus the release dates of the two versions involved.
    /// <para>⚠️ Filled only when a check actually runs (the update assistant, or the context-menu action) –
    /// never automatically. An update check is a login-authenticated round trip to every device; doing it
    /// on every scan would be a great deal of traffic for a column most people glance at rarely. So the
    /// columns stay empty until asked, which is honest: empty means "not checked", not "up to date".</para></summary>
    private readonly Dictionary<string, UpdateState> _updates = new();

    /// <summary>What a discovery phase is doing. Deliberately a state and not a percentage: the enrichment
    /// sweeps (MNDP, mDNS, SSDP, ZON) run <b>in parallel</b> for a fixed listening window, so "47 %" would
    /// be an invention. The address scan is the only phase with a real count, and it reports one.</summary>
    public enum PhaseState { Pending, Running, Done, Skipped }

    /// <param name="Name">Stable identifier ("scan", "mndp", …) – the UI localises it.</param>
    /// <param name="Progress">0..1 where the phase can count, -1 where it cannot.</param>
    public sealed record ScanPhase(string Name, PhaseState State, double Progress);

    private readonly List<ScanPhase> _phases = new();

    /// <summary>The phases of the current (or last) scan, for a per-phase progress display.</summary>
    public IReadOnlyList<ScanPhase> Phases
    {
        get { lock (_lock) return _phases.ToList(); }
    }

    private void SetPhase(string name, PhaseState state, double progress = -1)
    {
        lock (_lock)
        {
            var i = _phases.FindIndex(p => p.Name == name);
            var phase = new ScanPhase(name, state, progress);
            if (i >= 0) _phases[i] = phase; else _phases.Add(phase);
        }
    }

    /// <summary>One device's update situation, as far as it has been established.</summary>
    /// <param name="Installed">Running version.</param>
    /// <param name="Latest">Version the channel offers.</param>
    /// <param name="Available">Whether Latest is actually newer.</param>
    /// <param name="InstalledDate">Release date of the running version ("" when the changelog had none).</param>
    /// <param name="LatestDate">Release date of the offered version.</param>
    public sealed record UpdateState(string Installed, string Latest, bool Available,
        string InstalledDate, string LatestDate, string LatestUrl = "");
    private readonly AppData _appData;
    private volatile bool _scanning;

    /// <summary>Suspends the background refresh while a long, device-touching operation runs.
    ///
    /// <para>⚠️ Reachability keeps going – that is a plain TCP connect and says whether a rebooting device
    /// is back. What this stops is the per-device probe pass: an update installs and reboots the device,
    /// and a refresh talking to it mid-reboot produces failures that describe the refresh's timing, not the
    /// device. A counter rather than a flag, so two overlapping callers cannot resume it early.</para></summary>
    private int _refreshSuspended;

    public IDisposable SuspendRefresh() => new RefreshSuspension(this);

    private sealed class RefreshSuspension : IDisposable
    {
        private readonly FleetService _fleet;
        private bool _done;
        public RefreshSuspension(FleetService fleet)
        {
            _fleet = fleet;
            Interlocked.Increment(ref fleet._refreshSuspended);
        }
        public void Dispose()
        {
            if (_done) return;              // idempotent: a double dispose must not resume early
            _done = true;
            Interlocked.Decrement(ref _fleet._refreshSuspended);
        }
    }
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
    /// <summary>⚠️ Detached copies, never the live instances. Callers read these outside the lock while the
    /// background probes keep mutating the real devices under it; handing out references made the lock
    /// meaningless for everything inside a <see cref="Device"/>. Copies cost a few allocations per call and
    /// remove a whole class of race. Mutations go through the fleet's own methods (SetLogin, Merge …), so
    /// nothing is lost by them being snapshots.</summary>
    public Device? RawDevice(string id) { lock (_lock) return Find(id)?.Clone(); }
    public IReadOnlyList<Device> RawDevices() { lock (_lock) return _devices.Select(d => d.Clone()).ToList(); }

    public (bool Scanning, double Progress, string Phase, int Count) Status
    {
        get { lock (_lock) return (_scanning, _scanning ? _progress : 0, _phase, _devices.Count); }
    }

    // ---- writes ----------------------------------------------------------------------------------

    /// <summary>How TikMan should reach a device's management interface.</summary>
    public enum ConnectMethod
    {
        /// <summary>Work it out: HTTPS on the standard port, falling back to SSH when the TLS handshake
        /// fails (which RouterOS does often). What almost everyone wants.</summary>
        Auto,
        /// <summary>HTTPS REST on an explicit port.</summary>
        Https,
        /// <summary>Plain HTTP REST on an explicit port. ⚠️ Credentials cross the network unencrypted, so
        /// this also needs "allow HTTP" in the settings.</summary>
        Http,
        /// <summary>SSH only, on an explicit port.</summary>
        Ssh,
    }

    /// <summary>Pins how a device is contacted, instead of letting the defaults decide.
    /// <para>Why this exists: a device whose web interface sits on a non-standard port (8443, 8006 …) could
    /// not be used at all – the defaults assume 443/80, so every login attempt went to a port with nothing
    /// on it. Reported on GitHub, and entirely fair.</para>
    /// <para><paramref name="port"/> is ignored for <see cref="ConnectMethod.Auto"/>: the whole point of
    /// auto is that it picks, and an editable port next to it would only imply otherwise.</para></summary>
    public bool SetConnection(string id, ConnectMethod method, int port)
    {
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return false;

            switch (method)
            {
                case ConnectMethod.Auto:
                    d.UseHttps = true;
                    d.Port = 443;
                    d.SshPort = 22;
                    break;
                case ConnectMethod.Https:
                    d.UseHttps = true;
                    d.Port = Sane(port, 443);
                    break;
                case ConnectMethod.Http:
                    d.UseHttps = false;
                    d.Port = Sane(port, 80);
                    break;
                case ConnectMethod.Ssh:
                    d.SshPort = Sane(port, 22);
                    break;
            }
            Persist();
            return true;
        }

        static int Sane(int p, int fallback) => p is > 0 and <= 65535 ? p : fallback;
    }

    /// <summary>The method a device is currently set to, inferred from its stored ports – there is no
    /// separate flag, and inferring keeps old configs working without a migration.</summary>
    public (ConnectMethod Method, int Port) ConnectionOf(string id)
    {
        var d = RawDevice(id);
        if (d is null) return (ConnectMethod.Auto, 0);
        if (d.UseHttps && d.Port == 443 && d.SshPort == 22) return (ConnectMethod.Auto, 0);
        if (d.UseHttps) return (ConnectMethod.Https, d.Port);
        return (ConnectMethod.Http, d.Port);
    }

    /// <summary>The stored password of a device in the clear, for pre-filling the credentials dialog
    /// ("" when none is stored or it cannot be decrypted – a blob written under another Windows profile).
    ///
    /// <para>⚠️ Handed out only so the dialog can show what is set: pre-filling a placeholder instead made
    /// an unedited save write the placeholder, and left "clear this login" indistinguishable from "leave it
    /// alone". The value is never logged and never persisted anywhere but the encrypted blob it came
    /// from.</para></summary>
    public string PasswordOf(string id)
    {
        string blob;
        lock (_lock) blob = Find(id)?.EncryptedPassword ?? "";
        return blob.Length == 0 ? "" : CredentialProtector.Unprotect(blob);
    }

    /// <summary>Whether plain HTTP may be offered at all – the "allow HTTP" setting. False means the
    /// credentials dialog does not even list it: an option that sends the password in the clear should not
    /// be one keystroke away when the user has switched it off globally.</summary>
    public bool HttpAllowed
    {
        get { lock (_lock) return _appData.AllowHttpFallback; }
    }

    /// <summary>Sets (or clears) a device's login. Returns false if the device is gone.</summary>
    public bool SetLogin(string id, string user, string password)
    {
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return false;
            d.Username = user;
            d.EncryptedPassword = password.Length > 0 ? CredentialProtector.Protect(password) : "";
            // ⚠️ A new password is a new attempt: drop the old verdict rather than leaving the row marked
            // broken until the re-read finishes. Otherwise fixing a typo still shows a cross for a while,
            // which reads as "still wrong".
            _loginFailure.Remove(WebId(d));
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops a device from the inventory (the user removed a stale entry). Returns false when it
    /// was already gone. A later scan can of course find it again – this only clears the current list.</summary>
    public bool RemoveDevice(string id)
    {
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return false;
            _devices.Remove(d);
            _online.Remove(id);
            _seenAlive.Remove(id);
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    private string? _scanTarget; // null = every local subnet; else the CIDR/range the caller picked
    private CancellationTokenSource? _scanCts;

    /// <summary>Starts a scan. <paramref name="target"/> is a CIDR ("192.168.1.0/24") or range; empty/null
    /// means every local subnet (from the real interface masks).</summary>
    public void StartScan(string? target = null)
    {
        lock (_lock)
        {
            if (_scanning) return;
            _scanning = true; _progress = -1; _phase = "Scanning"; _scanned = 0; _scanTotal = 0;
            _scanTarget = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            // ⚠️ A user-triggered scan is an explicit "read it again now", so drop the auto-latest-check
            // rate limiter: the meta phase re-checks the Latest/update column instead of keeping the value
            // it read within the last 30 minutes. The limiter still throttles the 30 s background monitor,
            // which does not clear it.
            _lastLatestCheck.Clear();
        }
        Changed?.Invoke();
        _ = Task.Run(RunScanAsync);
    }

    /// <summary>Cancels a running scan. Devices already found stay in the list – they were merged live as
    /// the sweep reported them – only the remaining work is abandoned. A no-op when nothing runs.</summary>
    public void StopScan()
    {
        CancellationTokenSource? cts;
        lock (_lock) cts = _scanning ? _scanCts : null;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { /* scan ended in the same instant */ }
    }

    /// <summary>The local IPv4 networks (real interface masks) for a UI adapter/subnet picker.</summary>
    public static IReadOnlyList<LocalSubnet> LocalSubnets() => NetworkInfo.GetLocalSubnets();

    /// <summary>This machine's address on the picked subnet, or "" when scanning every network. Adapter-bound
    /// probes (ZON) use it so they cover exactly the segment the user chose.</summary>
    private string SelectedHostAddress()
    {
        string? target;
        lock (_lock) target = _scanTarget;
        if (target is not { Length: > 0 }) return "";
        return NetworkInfo.GetLocalSubnets()
            .FirstOrDefault(s => string.Equals(s.Cidr, target, StringComparison.OrdinalIgnoreCase))
            .HostAddress ?? "";
    }

    /// <summary>A finished config backup: the suggested filename and the bytes to write, or a failure with
    /// a reason. The bytes can carry secrets (a Zyxel running-config has "admin-password cipher …"), so
    /// they are only ever written to the chosen file – never logged.</summary>
    public sealed record BackupData(bool Ok, string Message, string FileName, byte[] Bytes)
    {
        public static BackupData Fail(string message) => new(false, message, "", Array.Empty<byte>());
    }

    /// <summary>Decrypts a device's stored password, for the rare caller that has to hand it to something
    /// outside this process (an external client the user explicitly opted into). Empty when there is none.
    /// <para>⚠️ Every other operation authenticates in-process and must use those methods instead – this one
    /// takes the secret out of the credential store, so each call site needs its own justification. Never
    /// log or persist the result.</para></summary>
    public string PasswordFor(string id)
    {
        var d = RawDevice(id);
        return d is null || d.EncryptedPassword.Length == 0 ? "" : CredentialProtector.Unprotect(d.EncryptedPassword);
    }

    /// <summary>Reads a device's log over the encrypted SSH CLI, whatever the vendor speaks. Null when the
    /// device is unknown, has no login, or does not answer – the caller shows that as "no log" rather than
    /// an empty table.
    /// <para>Each vendor's reader returns the same <see cref="LogEntry"/> shape, so one log view serves all
    /// of them: RouterOS <c>/log print</c>, TP-Link JetStream <c>show logging buffer</c>.</para>
    /// <para><paramref name="maxEntries"/> caps what crosses the wire; a full log can be very large.</para></summary>
    public async Task<List<LogEntry>?> LoadLogsAsync(string id, int maxEntries = 200)
    {
        var d = RawDevice(id);
        if (d is null || d.EncryptedPassword.Length == 0) return null;
        var password = CredentialProtector.Unprotect(d.EncryptedPassword);
        if (password.Length == 0) return null;
        try
        {
            if (IsMikroTik(d))
                return await RouterOsSsh.GetLogAsync(d.Host, d.SshPort, d.Username, password, maxEntries).ConfigureAwait(false);
            if (IsTpLink(d))
                return await TpLinkSshConnector.GetLogAsync(d.Host, d.SshPort, d.Username, password, maxEntries).ConfigureAwait(false);
            if (IsZyxelSwitch(d))
                return await ZyxelSsh.GetLogAsync(d.Host, d.SshPort, d.Username, password, maxEntries).ConfigureAwait(false);
            if (IsZyxelFirewall(d))
                return await ZldSsh.GetLogAsync(d.Host, d.SshPort, d.Username, password, maxEntries).ConfigureAwait(false);
            return null;
        }
        catch { return null; }
    }

    /// <summary>Whether this device's log can be read at all – the property the UI gates its log tab on.
    /// <para>⚠️ A capability question, not "is it a MikroTik". It used to be answered by reusing
    /// <c>CanUpdate</c>, which happens to mean "MikroTik with a login" – so adding a second vendor that can
    /// do logs but not updates would have left its tab greyed out with the data sitting right there.</para></summary>
    public static bool CanReadLogs(Device d) =>
        d.EncryptedPassword.Length > 0 && (IsMikroTik(d) || IsTpLink(d) || IsZyxelSwitch(d) || IsZyxelFirewall(d));

    /// <summary>Opens an interactive SSH shell to a device with its stored login. Null when the device is
    /// unknown, has no login, or the connection fails. The password is used only to authenticate.</summary>
    public async Task<ITerminalSession?> OpenTerminalAsync(string id, uint cols, uint rows) =>
        (await OpenTerminalDiagnosticAsync(id, cols, rows).ConfigureAwait(false)).Session;

    /// <summary>Opens the shell and, on failure, keeps the reason (missing login, or SSH.NET's own message)
    /// so the GUI can show it and decide whether to fall back to the external client.</summary>
    /// <param name="user">Credentials typed for this session (a device with no stored login). They win over
    /// the stored ones and are <b>never</b> persisted or logged – exactly like the password an SSH client
    /// prompts for.</param>
    public async Task<SshTerminalSession.ConnectResult> OpenTerminalDiagnosticAsync(string id, uint cols, uint rows,
        string? user = null, string? password = null)
    {
        var d = RawDevice(id);
        if (d is null) return new SshTerminalSession.ConnectResult(null, "device not found");

        var loginUser = user is { Length: > 0 } ? user : d.Username;
        var loginPass = password is { Length: > 0 }
            ? password
            : (d.EncryptedPassword.Length > 0 ? CredentialProtector.Unprotect(d.EncryptedPassword) : "");
        if (loginPass.Length == 0) return new SshTerminalSession.ConnectResult(null, "no stored login");

        return await SshTerminalSession.ConnectDiagnosticAsync(d.Host, d.SshPort, loginUser, loginPass, cols, rows)
            .ConfigureAwait(false);
    }

    /// <summary>Exports a device's config: RouterOS <c>/export</c> over SSH (.rsc), a Zyxel switch's
    /// running-config over its SSH CLI (.cfg). Needs a stored login. Others aren't config-backup-capable.</summary>
    public async Task<BackupData> BackupConfigAsync(string id)
    {
        var d = RawDevice(id);
        if (d is null) return BackupData.Fail("Device not found.");
        if (d.EncryptedPassword.Length == 0) return BackupData.Fail("No login stored.");
        if (!IsMikroTik(d) && !IsZyxelSwitch(d) && !IsTpLink(d) && !IsZyxelFirewall(d))
            return BackupData.Fail("Config backup is only available for MikroTik, Zyxel and TP-Link devices.");

        var password = CredentialProtector.Unprotect(d.EncryptedPassword);
        if (password.Length == 0) return BackupData.Fail("No login stored.");
        try
        {
            string? config; string ext;
            if (IsZyxelSwitch(d))
            {
                config = await ZyxelSsh.GetRunningConfigAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                ext = ".cfg";
            }
            else if (IsZyxelFirewall(d))
            {
                // ZLD firewall: "show running-config" over the SSH CLI. ⚠️ Carries "… password … cipher …"
                // and pre-shared keys; goes straight to the backup file, never logged. ZLD has no binary
                // backup artefact – this config IS the whole backup.
                config = await ZldSsh.GetRunningConfigAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                ext = ".conf";
            }
            else if (IsTpLink(d))
            {
                // TP-Link JetStream: "show running-config" over the SSH CLI. ⚠️ It carries account password
                // hashes – which belong in the backup file and nowhere else, so the text goes straight to
                // the caller and is never logged.
                config = await TpLinkSshConnector.GetRunningConfigAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                ext = ".cfg";
            }
            else
            {
                config = await SshConfigExport.GetAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                ext = ".rsc";
            }
            if (config is null) return BackupData.Fail("Config export over SSH failed.");
            var name = BackupNaming.SuggestFileName(d.Name, Model(d), d.Host, DateTime.Now, ext);
            return new BackupData(true, "", name, System.Text.Encoding.UTF8.GetBytes(config));
        }
        catch (Exception ex) { return BackupData.Fail("Backup failed: " + ex.Message); }
    }

    /// <summary>Fetches a device's binary full backup (.backup) – RouterOS only, entirely over SSH/SCP
    /// (<see cref="BackupService"/>). Needs a stored login. The bytes are staged in a temp file, read back,
    /// and the temp file is deleted; they can hold secrets, so they are never logged.</summary>
    public async Task<BackupData> FullBackupAsync(string id)
    {
        var d = RawDevice(id);
        if (d is null) return BackupData.Fail("Device not found.");
        if (!IsMikroTik(d)) return BackupData.Fail("Full backup is only available for MikroTik.");
        if (d.EncryptedPassword.Length == 0) return BackupData.Fail("No login stored.");
        var password = CredentialProtector.Unprotect(d.EncryptedPassword);
        if (password.Length == 0) return BackupData.Fail("No login stored.");

        var tmp = Path.Combine(Path.GetTempPath(), "tikman-" + Guid.NewGuid().ToString("N") + ".backup");
        try
        {
            await BackupService.DownloadFullBackupAsync(d, password, BackupMethod.Ssh, d.SshPort, tmp).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(tmp).ConfigureAwait(false);
            var name = BackupNaming.SuggestFileName(d.Name, Model(d), d.Host, DateTime.Now, ".backup");
            return new BackupData(true, "", name, bytes);
        }
        catch (Exception ex) { return BackupData.Fail("Full backup failed: " + ex.Message); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temp cleanup is best effort */ } }
    }

    /// <summary>Checks a RouterOS device for updates – HTTPS-REST first, then the SSH CLI when the TLS
    /// handshake is broken (same secure order the WPF client uses). Null when it's not a MikroTik, has no
    /// login, or neither transport answered.</summary>
    /// <param name="channel">Release channel to switch to before checking (stable / long-term / testing /
    /// development). Null or empty checks without touching the device's channel.</param>
    /// <summary>Runs a check and remembers the result so the device list can show it. Also looks the two
    /// versions' release dates up in the changelog – "7.24.1 available" says much less than "7.24.1, from
    /// three days ago" when deciding whether to install now or wait.</summary>
    public async Task<UpdateInfo?> CheckAndRememberAsync(string id, string? channel = null)
    {
        // Non-MikroTik switches have no update API. For the vendors whose public download page carries the
        // version (TP-Link reliably, Zyxel best-effort), read the latest off it and remember it the same way,
        // so the Latest column fills in and its click has a target. No MikroTik-style UpdateInfo comes back.
        var dev = RawDevice(id);
        if (dev is not null && !IsMikroTik(dev) && (IsTpLink(dev) || IsZyxelSwitch(dev)))
        {
            await CheckLatestFromWebAsync(dev).ConfigureAwait(false);
            return null;
        }

        var info = await CheckUpdateAsync(id, channel).ConfigureAwait(false);
        if (info is null) return null;

        // Best effort: no changelog (offline, or a version the list doesn't carry) simply means no date.
        var installedDate = await ReleaseDateAsync(info.InstalledVersion).ConfigureAwait(false);
        var latestDate = await ReleaseDateAsync(info.LatestVersion).ConfigureAwait(false);

        lock (_lock)
            _updates[id] = new UpdateState(info.InstalledVersion, info.LatestVersion, info.UpdateAvailable,
                installedDate, latestDate);
        Changed?.Invoke();
        return info;
    }

    private static async Task<string> ReleaseDateAsync(string version)
    {
        if (version.Length == 0) return "";
        try
        {
            var date = await ChangelogClient.GetReleaseDateAsync(version, CancellationToken.None).ConfigureAwait(false);
            return date is { } d ? d.ToString("yyyy-MM-dd") : "";
        }
        catch { return ""; }
    }

    /// <summary>Reads the latest firmware for a non-MikroTik switch off its vendor's download page and stores
    /// it as an <see cref="UpdateState"/>. Best effort: a page that can't be parsed (client-rendered, or an
    /// EOL model whose files were pulled) still stores the download URL, so the Latest column shows a "manual
    /// search" link instead of a number. Never throws.</summary>
    private async Task CheckLatestFromWebAsync(Device d)
    {
        // ⚠️ TP-Link reports its model only over the login CLI ("show system-info"); the passive scan leaves
        // it blank. So a latest-check that runs before a full scan has read the switch (straight after
        // setting credentials, or from the Update tab) has no model, and the firmware URL degrades to the
        // generic download hub. Read the facts on demand first, so "with a login it just works" holds no
        // matter the order. Best-effort: no login or a failed read simply falls through with what we have.
        if (IsTpLink(d) && Model(d).Length == 0 && d.EncryptedPassword.Length > 0)
        {
            var pw = CredentialProtector.Unprotect(d.EncryptedPassword);
            if (pw.Length > 0)
            {
                try
                {
                    d.Port = d.SshPort;   // GetFactsAsync connects to Device.Port; d is a RawDevice clone.
                    var f = await TpLinkSshConnector.GetFactsAsync(d, pw).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (f.Model.Length > 0) d.ExtraInfo["Modell"] = f.Model;
                        if (f.HardwareVersion.Length > 0) d.ExtraInfo["Hardware-Version"] = f.HardwareVersion;
                        if (f.FirmwareVersion.Length > 0 && !d.ExtraInfo.ContainsKey("Firmware"))
                            d.ExtraInfo["Firmware"] = f.FirmwareVersion;
                    }
                }
                catch { /* SSH off / wrong creds / unreachable – fall through with what we have */ }
            }
        }

        string id, vendor, model, hwRev, installed;
        lock (_lock)
        {
            id = WebId(d);
            vendor = Vendor(d);
            model = Model(d);
            hwRev = d.ExtraInfo.TryGetValue("Hardware-Version", out var hv) && hv.Length > 0 ? hv : d.HardwareRevision;
            installed = d.ExtraInfo.TryGetValue("Firmware", out var fw) ? fw
                : d.ExtraInfo.TryGetValue("Version", out var ver) ? ver : "";
        }

        LatestFirmware? latest;
        try { latest = await FirmwareLatest.QueryAsync(vendor, model, hwRev, installed).ConfigureAwait(false); }
        catch { latest = null; }
        if (latest is null) return;

        var available = latest.Parsed && FirmwareLatest.IsNewer(latest.Version, installed);
        lock (_lock)
            _updates[id] = new UpdateState(installed, latest.Version, available, "", "", latest.SourceUrl);
        Changed?.Invoke();
    }

    public async Task<UpdateInfo?> CheckUpdateAsync(string id, string? channel = null)
    {
        var d = RawDevice(id);
        if (d is null || !IsMikroTik(d) || d.EncryptedPassword.Length == 0) return null;
        var password = CredentialProtector.Unprotect(d.EncryptedPassword);
        if (password.Length == 0) return null;
        try
        {
            using var client = new RouterOsClient(d.Host, d.Port, d.UseHttps, d.Username, password, ignoreCertErrors: true);
            return channel is { Length: > 0 }
                ? await client.SetChannelAndCheckAsync(channel).ConfigureAwait(false)
                : await client.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch
        {
            // ⚠️ The fallback needs its own guard. REST failing is the normal path into here, and SSH then
            // throwing (host gone, auth refused, timeout) escaped this method into an `async void` handler –
            // i.e. an unhandled exception on the sync context, which takes the process down. A check that
            // cannot reach the device must return "no answer", not kill the app.
            try
            {
                return await RouterOsSsh.CheckForUpdatesAsync(d.Host, d.SshPort, d.Username, password, channel)
                    .ConfigureAwait(false);
            }
            catch { return null; }
        }
    }

    /// <summary>Remembers which release channel a device should use. Persisted with the device, so the
    /// assistant's per-device choice survives a restart.</summary>
    public void SetUpdateChannel(string id, string channel)
    {
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return;
            d.UpdateChannel = channel ?? "";
            Persist();
        }
    }

    /// <summary>The channel stored for a device, or "" when it follows the global default.</summary>
    public string UpdateChannelOf(string id) => RawDevice(id)?.UpdateChannel ?? "";

    /// <summary>The outcome of a credential test: whether it worked and a line to show the user.</summary>
    public sealed record LoginTest(bool Ok, string Message);

    /// <summary>Tries the given credentials against a device <b>without storing them</b>, so a password can
    /// be checked before it is saved – and a failure can be told apart from a wrong password.
    /// <para>⚠️ The password is used for this one connection and nothing else: not persisted, not logged,
    /// not kept after the call returns.</para>
    /// <para>Reports what came back (identity, board, version) rather than a bare "OK", because that is
    /// what proves the credentials reached <i>this</i> device and not something else on the address.</para></summary>
    public async Task<LoginTest> TestLoginAsync(string id, string user, string password)
    {
        var d = RawDevice(id);
        if (d is null) return new LoginTest(false, "Device not found.");
        if (password.Length == 0) return new LoginTest(false, "Enter a password first.");

        // MikroTik speaks REST; everything else we can only check by opening an SSH session.
        if (IsMikroTik(d))
        {
            try
            {
                using var client = new RouterOsClient(d.Host, d.Port, d.UseHttps, user, password, ignoreCertErrors: true);
                var resource = await client.GetSystemResourceAsync().ConfigureAwait(false);
                var identity = await client.GetIdentityAsync().ConfigureAwait(false);
                return new LoginTest(true, $"{identity} · {resource.BoardName} · RouterOS {resource.Version}");
            }
            catch (Exception ex)
            {
                // A broken TLS handshake is the usual RouterOS story – fall back to SSH before calling it
                // a bad password, since that is the transport a backup would use anyway.
                try
                {
                    var res = await RouterOsSsh.GetResourceAsync(d.Host, d.SshPort, user, password).ConfigureAwait(false);
                    if (res is not null)
                        return new LoginTest(true, $"{d.Name} · {res.BoardName} · RouterOS {res.Version} (over SSH)");
                }
                catch { /* fall through to the original error */ }
                return new LoginTest(false, ex.Message);
            }
        }

        // ⚠️ TP-Link is verified through its OWN connector, not the generic SshTerminalSession. That session
        // fails on a TP-Link even with the RIGHT password (the switch's slow handshake exceeds the terminal
        // timeout, and TPSSH does not do a plain shell the way the terminal expects) – so a correct login
        // was reported as broken. GetFactsAsync uses the shell path these switches actually support, so a
        // model/firmware coming back means the credentials work.
        if (IsTpLink(d))
        {
            d.Port = d.SshPort; d.Username = user;   // d is a private clone (RawDevice) – safe to adjust
            try
            {
                var facts = await TpLinkSshConnector.GetFactsAsync(d, password).ConfigureAwait(false);
                return facts.Model.Length > 0 || facts.FirmwareVersion.Length > 0
                    ? new LoginTest(true, $"{facts.Model} · {facts.FirmwareVersion}".Trim(' ', '·'))
                    : new LoginTest(false, "SSH login failed or the switch did not answer.");
            }
            catch (Exception ex) { return new LoginTest(false, ex.Message); }
        }

        // ⚠️ ZLD firewall through its OWN connector: its "show version" table is the proof the login reached
        // THIS firewall (model + firmware), and the interactive shell is the transport it supports.
        if (IsZyxelFirewall(d))
        {
            try
            {
                var info = await ZldSsh.GetInfoAsync(d.Host, d.SshPort, user, password).ConfigureAwait(false);
                return info is { } zf && (zf.Model.Length > 0 || zf.Firmware.Length > 0)
                    ? new LoginTest(true, $"{zf.Model} · {zf.Firmware}".Trim(' ', '·'))
                    : new LoginTest(false, "SSH login failed or the firewall did not answer.");
            }
            catch (Exception ex) { return new LoginTest(false, ex.Message); }
        }

        try
        {
            var session = await SshTerminalSession.ConnectAsync(d.Host, d.SshPort, user, password, 80, 24)
                .ConfigureAwait(false);
            if (session is null) return new LoginTest(false, "SSH refused the login.");
            session.Dispose();
            return new LoginTest(true, "SSH login accepted.");
        }
        catch (Exception ex) { return new LoginTest(false, ex.Message); }
    }

    /// <summary>CPU/RAM readings collected for a device so far, oldest first. A copy: the caller renders it
    /// on the UI thread while the monitor keeps appending under the lock.</summary>
    public IReadOnlyList<ResourceSnapshot> HistoryOf(string id)
    {
        lock (_lock)
            return _history.TryGetValue(id, out var points)
                ? points.ToList()
                : Array.Empty<ResourceSnapshot>();
    }

    /// <summary>One-off reachability check, for waiting on a device that is rebooting after an update.
    /// Uses a port the device was seen answering on – privilege-free TCP, no ICMP.
    /// <para>False when the device is unknown or has no known open port: "we cannot ask" reads the same as
    /// "not back yet", and the caller has a timeout either way.</para></summary>
    public async Task<bool> IsReachableAsync(string id)
    {
        var d = RawDevice(id);
        if (d is null || d.OpenPorts.Count == 0) return false;
        return await Reachability.TcpProbeAsync(d.Host, d.OpenPorts[0]).ConfigureAwait(false);
    }

    /// <summary>Installs the pending RouterOS update (which downloads and reboots) – SSH first, REST as a
    /// fallback. Returns false when it's not a MikroTik, has no login, or neither transport triggered it.
    /// ⚠️ The device reboots; the caller should expect it to drop off the network briefly.</summary>
    public async Task<bool> InstallUpdateAsync(string id)
    {
        var d = RawDevice(id);
        if (d is null || !IsMikroTik(d) || d.EncryptedPassword.Length == 0) return false;
        var password = CredentialProtector.Unprotect(d.EncryptedPassword);
        if (password.Length == 0) return false;
        if (await RouterOsSsh.InstallUpdateAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false))
            return true;
        try
        {
            using var client = new RouterOsClient(d.Host, d.Port, d.UseHttps, d.Username, password, ignoreCertErrors: true);
            await client.InstallUpdateAsync().ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    // ---- scanning + enrichment -------------------------------------------------------------------

    private async Task RunScanAsync()
    {
        try
        {
            CancellationToken token;
            string? picked; int pingTimeout, pingRetries, socketLimit; bool scanUnpingable; string customPorts;
            lock (_lock)
            {
                token = _scanCts?.Token ?? CancellationToken.None;
                picked = _scanTarget;
                pingTimeout = _appData.PingTimeoutMs;
                pingRetries = _appData.PingRetries; // ⚠️ was never forwarded – the retry setting did nothing
                scanUnpingable = _appData.ScanUnpingableHosts;
                socketLimit = _appData.MaxConcurrentProbes;
                customPorts = _appData.CustomPorts ?? "";
            }
            SubnetScanner.SetCustomPorts(customPorts);
            // Applied per scan so a changed setting takes effect without a restart.
            SubnetScanner.SetSocketLimit(socketLimit);

            // Declare the phases up front so the UI can show the whole plan, not just what has started.
            // ZON is listed as skipped when the capture layer is missing rather than silently omitted –
            // "not running because Npcap is absent" is information the user wants.
            lock (_lock) _phases.Clear();
            SetPhase("scan", PhaseState.Running, 0);
            bool willEnrich; lock (_lock) willEnrich = !_appData.SimpleScanMode;
            foreach (var p in new[] { "mndp", "mdns", "ssdp" })
                SetPhase(p, willEnrich ? PhaseState.Pending : PhaseState.Skipped);
            SetPhase("zon", !willEnrich || !ZdpScanner.IsAvailable() ? PhaseState.Skipped : PhaseState.Pending);
            SetPhase("probe", PhaseState.Pending);
            var targets = picked ?? LocalScanTargets();

            // ⚠️ Count the hosts BEFORE the scan starts and under the lock: the progress callbacks run on
            // pool threads, and an unsynchronised write here left them reading _scanTotal == 0 for the whole
            // scan, so the bar stayed indeterminate while hosts were completing.
            lock (_lock) _scanTotal = CountTargets(targets);

            // ⚠️ Notify at most ~5×/s. This fired once per scanned host – ~2000 events on a /21, each one
            // rebuilding both device collections on the UI thread, which is what made the window crawl
            // during a scan. Progress is still counted for every host; only the notification is coalesced.
            var lastNotify = 0L;
            var onHost = new Progress<int>(_ =>
            {
                bool notify;
                lock (_lock)
                {
                    _scanned++;
                    if (_scanTotal > 0) _progress = Math.Min(1.0, (double)_scanned / _scanTotal);
                    var i = _phases.FindIndex(p => p.Name == "scan");
                    if (i >= 0) _phases[i] = _phases[i] with { Progress = _progress };
                    var now = Environment.TickCount64;
                    notify = now - lastNotify >= 200 || _scanned >= _scanTotal;
                    if (notify) lastNotify = now;
                }
                if (notify) Changed?.Invoke();
            });

            // Simple mode = the address scan and nothing else: no MNDP/ZON/mDNS/UPnP packets and no
            // SNMP/WMI/web probes, only ordinary connections. Meant for locked-down company networks where
            // the extra chatter is unwelcome. Reachability still runs – it is a plain TCP connect.
            bool simple;
            lock (_lock) simple = _appData.SimpleScanMode;

            // ⚠️ The listeners start WITH the address sweep, not after it. They are passive: each one binds a
            // socket and waits for devices to announce themselves, so the only thing that running them later
            // achieved was making the user wait for the sweep first. Started here they overlap it completely
            // and a /21 finishes roughly a listening window sooner.
            // Their results are still applied *after* the sweep has been merged, which is what actually
            // mattered: the sweep establishes the devices, the announcements enrich them.
            var listeners = simple ? null : StartListeners();

            // ⚠️ Devices land in the list as they are found, not when the sweep ends. On a /21 the sweep
            // takes minutes, and waiting for it meant staring at an empty window while the progress bar
            // claimed things were happening. The scanner has always reported each hit – this simply stopped
            // discarding that and used it.
            // The notification is coalesced the same way as the progress counter: merging is cheap, but
            // every notification rebuilds both device collections on the UI thread.
            var lastRowNotify = 0L;
            var onFound = new Progress<DiscoveredDevice>(d =>
            {
                bool notify;
                lock (_lock)
                {
                    Merge(d);
                    // No Persist() here: the final merge below writes once, and saving per device would put
                    // a file write in the middle of a sweep.
                    var now = Environment.TickCount64;
                    notify = now - lastRowNotify >= 250;
                    if (notify) lastRowNotify = now;
                }
                if (notify) Changed?.Invoke();
            });

            // ⚠️ The token rides the sweep itself, so a Stop takes effect within one host probe – not at the
            // next phase boundary. Cancellation surfaces as OperationCanceledException and lands in the
            // catch below; everything found up to that point was already merged live by onFound.
            var found = await SubnetScanner.ScanAsync(targets, onFound: onFound, onHostScanned: onHost,
                ct: token, pingTimeoutMs: pingTimeout, pingRetries: pingRetries, scanUnpingable: scanUnpingable);
            lock (_lock) { foreach (var f in found) Merge(f); Persist(); }
            SetPhase("scan", PhaseState.Done, 1);
            Changed?.Invoke();

            if (listeners is not null)
                await ApplyListenersAsync(listeners, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            // Meta phase: the per-device probes (SNMP/web/vendor/SMB), the IPv6 service scan, and the
            // reachability pass. Progress counts probed devices, so the bar climbs device by device
            // instead of sitting still for the longest stretch of the scan.
            SetPhase("probe", PhaseState.Running, 0);
            var lastMetaNotify = 0L;
            void MetaProgress(double p)
            {
                SetPhase("probe", PhaseState.Running, Math.Min(0.99, p));
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref lastMetaNotify) < 200) return;
                Volatile.Write(ref lastMetaNotify, now);
                try { Changed?.Invoke(); } catch { }
            }

            if (!simple)
            {
                string community;
                lock (_lock) community = SnmpCommunityLocked();
                // Every tail step drives the bar so it keeps moving to the end instead of freezing.
                // ⚠️ The IPv6 steps get a generous band (0.55 → 0.9): measured, they are often the slowest
                // part (~10 s for a handful of silent v6 addresses, one timeout each), and that stretch
                // sitting still was the "stuck near 80 %" the user saw. Per-device probes 0 → 0.55, v6
                // services 0.55 → 0.75, v6 per-address enrichment 0.75 → 0.9, reachability 0.9 → 1.
                await ProbeDevicesAsync(community, token, waitForTurn: true, p => MetaProgress(p * 0.55))
                    .ConfigureAwait(false);
                await ProbeIpv6ServicesAsync(token, p => MetaProgress(0.55 + p * 0.2)).ConfigureAwait(false);
                await ProbeIpv6MetaAsync(community, token, p => MetaProgress(0.75 + p * 0.15)).ConfigureAwait(false);
                await ProbeAllAsync(p => MetaProgress(0.9 + p * 0.1)).ConfigureAwait(false);
            }
            else await ProbeAllAsync(p => MetaProgress(p)).ConfigureAwait(false);
            SetPhase("probe", PhaseState.Done, 1);
        }
        catch { /* a scan that fails – or is stopped – leaves the found-so-far list in place */ }
        finally
        {
            lock (_lock) { _scanning = false; _phase = ""; _progress = 0; }
            Changed?.Invoke();
        }
    }

    /// <summary>Whether a discovery describes a device already in the list.
    ///
    /// <para>⚠️ Matching on the MAC alone is not enough, and it produced <b>duplicate rows</b>: a router
    /// has a separate MAC per interface. The subnet scan learns the MAC from ARP – the interface facing
    /// this machine – while MNDP announces the device's <i>own</i> MAC, which is usually a different one.
    /// Two MACs, one box, two rows: one with the ports, one with the board name and version.</para>
    ///
    /// <para>So the address counts too. On a working network one address is one device; a duplicate IP is
    /// a misconfiguration, and even then showing a single entry is the better answer.</para></summary>
    public static bool IsSameDevice(Device d, DiscoveredDevice f) =>
        (f.MacAddress.Length > 0 && d.MacAddress.Length > 0 &&
         string.Equals(d.MacAddress, f.MacAddress, StringComparison.OrdinalIgnoreCase))
        || (f.IpAddress.Length > 0 && d.Host == f.IpAddress);

    /// <summary>Files an IPv6 neighbour under the device it belongs to.
    ///
    /// <para>⚠️ Deliberately not <see cref="Merge"/>: that sets <c>Host</c> from the discovery, which for an
    /// IPv6 record would overwrite the device's IPv4 address – the one everything else (scanning, REST, SSH,
    /// the forwarding tables) is keyed on. IPv6 addresses are additional, so they go to
    /// <c>AltAddresses</c>. One NIC commonly has several (global, ULA, link-local, privacy), which is why
    /// they are collected rather than replaced.</para></summary>
    private void MergeIpv6(DiscoveredDevice f)
    {
        if (f.IpAddress.Length == 0 || !f.IpAddress.Contains(':')) return;

        var owner = f.MacAddress.Length > 0
            ? _devices.FirstOrDefault(d => d.MacAddress.Length > 0 &&
                string.Equals(d.MacAddress, f.MacAddress, StringComparison.OrdinalIgnoreCase))
            : null;

        if (owner is null)
        {
            // An address we cannot attach to anything known: a v6-only neighbour. It still gets a row, the
            // same as in the WPF client – but keyed on the v6 address, so it does not collide with a device
            // that simply has not been scanned yet.
            if (_devices.Any(d => d.Host == f.IpAddress || d.AltAddresses.Contains(f.IpAddress))) return;
            // ⚠️ Not while a specific range is being scanned. Neighbour discovery is link-local: it reports
            // what is on the local segment regardless of the target, so scanning a VPN range turned up the
            // operator's own machines under their ISP prefix. An IPv6 address can never be inside an IPv4
            // target, so there is nothing to test – the question is only whether the user asked for a
            // particular range at all.
            if (!string.IsNullOrWhiteSpace(_scanTarget)) return;
            owner = new Device { Host = f.IpAddress, MacAddress = f.MacAddress };
            _devices.Add(owner);
            // Being in the neighbour table is how this device was found at all – that counts as having
            // answered (yellow), like any other discovery. Without this, v6-only neighbours sat grey
            // forever: they have no open port, so no probe ever upgrades or downgrades them.
            _seenAlive.Add(WebId(owner));
            return;
        }

        if (owner.Host == f.IpAddress || owner.AltAddresses.Contains(f.IpAddress)) return;
        owner.AltAddresses.Add(f.IpAddress);
    }

    /// <summary>The addresses the running scan was asked to cover, or null when it covers every local
    /// subnet. Cached per target string – parsing 65k addresses once per scan is fine, once per discovered
    /// device is not.</summary>
    private string? _targetSetFor;
    private HashSet<uint>? _targetSet;

    /// <summary>Whether an address is inside the range the user actually asked for.
    ///
    /// <para>⚠️ This exists because the discovery listeners are <b>not addressable</b>. MNDP, ZON, mDNS and
    /// SSDP are broadcast and multicast protocols: they go out of every interface and answer with whatever
    /// is on the wire, which has nothing to do with the range being scanned. That is correct behaviour for
    /// them – the bug was accepting the answers unconditionally. Scanning a VPN range therefore filled the
    /// list with the operator's own office LAN, and those rows look exactly like findings.</para>
    ///
    /// <para>Built with <see cref="SubnetScanner.EnumerateTargets"/> – the same parser the sweep itself
    /// uses – so "in the range" cannot come to mean something subtly different here (short-form ranges,
    /// comma-separated parts, the excluded network and broadcast addresses).</para>
    ///
    /// <para>Caller must hold the lock. True when no target is set: an "all local subnets" scan is exactly
    /// the case where hearing the local network is the point.</para></summary>
    private bool InScanTarget(string ip)
    {
        var target = _scanTarget;
        if (string.IsNullOrWhiteSpace(target)) return true;
        if (!System.Net.IPAddress.TryParse(ip, out var addr) ||
            addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;   // v6 is never inside an IPv4 target – see the callers

        if (_targetSetFor != target)
        {
            try
            {
                _targetSet = SubnetScanner.EnumerateTargets(target)
                    .Select(a => { var b = a.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; })
                    .ToHashSet();
            }
            // An unparseable target already failed the scan itself; not a reason to also drop every
            // announcement, so fall back to the old behaviour of accepting them.
            catch (ArgumentException) { _targetSet = null; }
            _targetSetFor = target;
        }
        if (_targetSet is null) return true;

        var bytes = addr.GetAddressBytes();
        return _targetSet.Contains(((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3]);
    }

    private void Merge(DiscoveredDevice f)
    {
        // A MAC match is the stronger claim (it survives an address change), so it is preferred; the
        // address match is what catches the same box announcing itself under a second interface MAC.
        var existing =
            _devices.FirstOrDefault(d => f.MacAddress.Length > 0 && d.MacAddress.Length > 0 &&
                string.Equals(d.MacAddress, f.MacAddress, StringComparison.OrdinalIgnoreCase))
            ?? _devices.FirstOrDefault(d => f.IpAddress.Length > 0 && d.Host == f.IpAddress);

        // ⚠️ A device outside the scanned range may be UPDATED but never CREATED. The announcement is real –
        // MNDP and ZON heard it on the wire – but it answers a question the user did not ask, and a new row
        // is indistinguishable from a finding. Updating a row that is already there is different: that
        // device is in the list because an earlier scan found it, and fresher facts about it are welcome.
        if (existing is null && !InScanTarget(f.IpAddress)) return;

        var d = existing ?? new Device();
        d.Host = f.IpAddress;

        // ⚠️ Keep the first MAC rather than the last. The subnet scan runs before the discovery sweeps, so
        // the one already stored came from ARP – the interface on this segment, which is exactly the MAC a
        // switch's forwarding table sees. Letting MNDP overwrite it with another interface's MAC would
        // quietly break the physical map's placement of that device. The other MAC is kept as a fact.
        if (f.MacAddress.Length > 0)
        {
            if (d.MacAddress.Length == 0) d.MacAddress = f.MacAddress;
            else if (!string.Equals(d.MacAddress, f.MacAddress, StringComparison.OrdinalIgnoreCase))
                d.ExtraInfo["Weitere MAC"] = f.MacAddress;
        }
        if (f.Identity.Length > 0 && d.Name.Length == 0) d.Name = f.Identity;
        if (f.Board.Length > 0)
        {
            d.ExtraInfo["Modell"] = f.Board;
            // ⚠️ The source names the vendor: MNDP is only ever answered by MikroTik, ZON only by Zyxel.
            // Labelling every board-carrying discovery "MikroTik" would re-tag every Zyxel switch.
            d.ExtraInfo["Hersteller (Web)"] = f.Source == "ZON" ? "Zyxel" : "MikroTik";
        }
        else if (f.IsLikelyMikroTik && !d.ExtraInfo.ContainsKey("Hersteller (Web)"))
            d.ExtraInfo["Hersteller (Web)"] = "MikroTik";
        if (f.Version.Length > 0) d.ExtraInfo["Version"] = f.Version;
        if (f.OpenPorts.Count > 0) d.OpenPorts = f.OpenPorts.Distinct().OrderBy(p => p).ToList();
        if (existing is null) _devices.Add(d);

        // A merge IS an answer – the sweep got a response, or the device announced itself (MNDP/ZON).
        // Without this, every device sat grey ("not checked") through the whole sweep although it had just
        // spoken, because the reachability pass only runs at the end of the scan. Deliberately not written
        // into _online: one answer during discovery is evidence of life, not an ongoing validation – the
        // UI shows it as its own (yellow) state until a real probe upgrades it to green or red.
        _seenAlive.Add(WebId(d));
    }

    /// <summary>The four passive listeners, in flight.</summary>
    private sealed record Listeners(
        Task<List<DiscoveredDevice>> Mndp,
        Task<Dictionary<string, MdnsScanner.MdnsInfo>> Mdns,
        Task<Dictionary<string, SsdpScanner.SsdpInfo>> Ssdp,
        Task<List<DiscoveredDevice>> Zdp,
        Task<List<DiscoveredDevice>> Ipv6);

    /// <summary>Opens the listening windows. These do not query anything device by device – each binds a
    /// socket and collects whatever announces itself – so they are started at the same moment as the address
    /// sweep and simply run alongside it.</summary>
    private Listeners StartListeners()
    {
        // ⚠️ Every adapter-bound probe follows the picked subnet. These are broadcast/multicast protocols
        // that otherwise go out of EVERY interface, so choosing one network in the UI would still drag in
        // devices from every other segment this machine is attached to.
        var zdpHost = SelectedHostAddress();
        var ct = CancellationToken.None;

        // All four listen in parallel for a fixed window, so they go Running together.
        foreach (var p in new[] { "mndp", "mdns", "ssdp" }) SetPhase(p, PhaseState.Running);
        if (ZdpScanner.IsAvailable()) SetPhase("zon", PhaseState.Running);
        Changed?.Invoke();

        var listeners = new Listeners(
            Safe(() => MndpScanner.DiscoverAsync(TimeSpan.FromSeconds(4), null, ct, zdpHost), new List<DiscoveredDevice>()),
            Safe(() => MdnsScanner.DiscoverAsync(TimeSpan.FromSeconds(4), ct, zdpHost), new Dictionary<string, MdnsScanner.MdnsInfo>()),
            Safe(() => SsdpScanner.DiscoverAsync(TimeSpan.FromSeconds(4), ct, zdpHost), new Dictionary<string, SsdpScanner.SsdpInfo>()),
            // ZON (Zyxel, raw Ethernet) only when the capture layer is there – Npcap on Windows, libpcap on
            // Unix. It is layer 2, so it only ever sees devices in the same segment as this machine.
            ZdpScanner.IsAvailable()
                ? Safe(() => ZdpScanner.DiscoverAsync(TimeSpan.FromSeconds(4), null, ct, zdpHost), new List<DiscoveredDevice>())
                : Task.FromResult(new List<DiscoveredDevice>()),
            // ⚠️ IPv6 belongs here, not only in the WPF client. It used to run solely in that client's own
            // discovery code, so every consumer of this service – the Avalonia client and the headless host –
            // showed a permanently empty IPv6 view. Nothing was wrong with those views; the addresses were
            // never collected in the first place.
            Safe(() => Ipv6Discovery.DiscoverAsync(null, ct), new List<DiscoveredDevice>()));

        // Each protocol reports Done the moment its own listening window closes – not in one block when
        // all of them have. The windows overlap the sweep, so by the time the sweep ends most are already
        // ticked off, and the combined bar can credit each protocol its share as it finishes.
        MarkDoneWhenFinished("mndp", listeners.Mndp);
        MarkDoneWhenFinished("mdns", listeners.Mdns);
        MarkDoneWhenFinished("ssdp", listeners.Ssdp);
        if (ZdpScanner.IsAvailable()) MarkDoneWhenFinished("zon", listeners.Zdp);
        return listeners;
    }

    private void MarkDoneWhenFinished(string phase, Task task) =>
        task.ContinueWith(_ =>
        {
            SetPhase(phase, PhaseState.Done, 1);
            try { Changed?.Invoke(); } catch { /* a subscriber must not kill the listener chain */ }
        }, TaskScheduler.Default);

    /// <summary>Waits for the listening windows to close and folds what they heard into the device list,
    /// then runs the per-device probes.
    ///
    /// <para>⚠️ Applied only once the address sweep has been merged. That ordering is the part worth
    /// keeping: the sweep decides which devices exist, so rows stop appearing when it ends, and the
    /// announcements then fill in names and models on rows that are already there. Starting the listeners
    /// early changes when they <i>listen</i>, not when the list settles.</para></summary>
    private async Task ApplyListenersAsync(Listeners l, CancellationToken ct)
    {
        await Task.WhenAll(l.Mndp, l.Mdns, l.Ssdp, l.Zdp, l.Ipv6).ConfigureAwait(false);

        // Belt and braces – MarkDoneWhenFinished has normally ticked these off individually already.
        foreach (var p in new[] { "mndp", "mdns", "ssdp" }) SetPhase(p, PhaseState.Done, 1);
        if (ZdpScanner.IsAvailable()) SetPhase("zon", PhaseState.Done, 1);

        lock (_lock)
        {
            foreach (var f in l.Mndp.Result) Merge(f);
            foreach (var f in l.Zdp.Result) Merge(f);
            foreach (var f in l.Ipv6.Result) MergeIpv6(f);
            foreach (var d in _devices)
            {
                if (l.Mdns.Result.TryGetValue(d.Host, out var m)) ApplyMdns(d, m);
                if (l.Ssdp.Result.TryGetValue(d.Host, out var s)) ApplySsdp(d, s);
            }
            Persist();
        }
        Changed?.Invoke();
        // ⚠️ The per-device probes no longer run here. They are the scan's meta phase and belong to the
        // "probe" phase in RunScanAsync – with a progress count – not hidden inside "applying listeners",
        // where the longest part of the scan ran while the phase display claimed everything was done.
    }

    /// <summary>Port-scans every IPv6 address in its own right.
    ///
    /// <para>⚠️ The <b>full</b> service list, not just the ports the device showed over IPv4. A service can
    /// be bound to one address only – something listening exclusively on a v6 address is invisible to the
    /// IPv4 scan, so deriving the v6 ports from the v4 result would guarantee never finding exactly the case
    /// that makes a per-address view worth having. Likewise, the addresses of one device are not
    /// interchangeable: a firewall may accept on the global address and drop on the ULA.</para>
    ///
    /// <para>Affordable because the shape is the opposite of the IPv4 sweep: a handful of addresses, all
    /// ports of one address attempted at once, and a short timeout. An address costs about one timeout in
    /// total, not one per port.</para></summary>
    private async Task ProbeIpv6ServicesAsync(CancellationToken ct, Action<double>? onProgress = null)
    {
        var ports = SubnetScanner.ServicePorts.Select(sp => sp.Port)
            .Concat(SubnetScanner.CustomPorts)
            .Distinct().ToList();

        // ⚠️ Without a zone index nothing on a normal LAN gets probed at all: the neighbour cache mostly
        // yields link-local addresses, and those cannot be dialled by address alone (the same fe80:: can
        // exist on every interface). The zone is the interface the scan is bound to.
        var scope = Ipv6ScopeId();

        List<string> addresses = new();
        lock (_lock)
            foreach (var d in _devices)
                foreach (var a in Ipv6AddressesOf(d))
                    if (Ipv6ServiceProbe.IsProbeable(a, scope) && !addresses.Contains(a)) addresses.Add(a);

        if (addresses.Count == 0) { onProgress?.Invoke(1); return; }

        var done = 0;
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(addresses.Select(async address =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var open = await Ipv6ServiceProbe.ProbeAsync(address, ports, scopeId: scope, ct: ct)
                    .ConfigureAwait(false);
                lock (_lock) _ipv6Ports[address] = open;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort */ }
            finally
            {
                gate.Release();
                // ⚠️ Reported so the progress bar keeps moving through this step. A silent v6 address takes
                // one full timeout (~one per address, not per port), so a few of them add up to seconds –
                // long enough that a frozen bar here read as "stuck at 80 %" after the meta scan.
                onProgress?.Invoke(Interlocked.Increment(ref done) / (double)addresses.Count);
            }
        })).ConfigureAwait(false);
    }

    /// <summary>Enriches each IPv6 address the same way the IPv4 pass enriches a device – <b>over that
    /// address</b>: web fingerprint, TLS certificate, SMB identity, SMB shares, SNMP.
    ///
    /// <para>⚠️ Nothing here is copied from the device's IPv4 result, which is the entire point. A dual-stack
    /// host frequently binds services to one family only, serves a different web UI on each, or is passed by
    /// the firewall on its global address and dropped on its ULA. Filling the v6 rows from the v4 pass makes
    /// every address look alike – including the ones that answer nothing at all.</para>
    ///
    /// <para>Only addresses that answered on at least one port are asked: the meta probes each cost a real
    /// round trip, and an address that refused every port of the service sweep is not going to answer HTTP.
    /// So the cost tracks what is actually there, not the size of the neighbour cache.</para></summary>
    private async Task ProbeIpv6MetaAsync(string community, CancellationToken ct, Action<double>? onProgress = null)
    {
        var scope = Ipv6ScopeId();
        List<string> addresses;
        Dictionary<string, string> names = new();
        lock (_lock)
        {
            addresses = _ipv6Ports.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
            foreach (var d in _devices)
                foreach (var a in Ipv6AddressesOf(d))
                    names[a] = d.Name;
        }
        if (addresses.Count == 0) { onProgress?.Invoke(1); return; }

        var done = 0;
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(addresses.Select(async address =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var facts = await ProbeOneIpv6Async(address, scope, community, ct).ConfigureAwait(false);
                lock (_lock) _ipv6Meta[address] = facts;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort per address */ }
            finally
            {
                gate.Release();
                onProgress?.Invoke(Interlocked.Increment(ref done) / (double)addresses.Count);
            }
        })).ConfigureAwait(false);
    }

    private async Task<Ipv6Facts> ProbeOneIpv6Async(string address, long scope, string community,
        CancellationToken ct)
    {
        // The form a socket needs: a link-local is meaningless without the zone of the link it lives on.
        var dial = Ipv6ServiceProbe.WithZone(address, scope);
        List<int> ports;
        lock (_lock) ports = _ipv6Ports.TryGetValue(address, out var p) ? p.ToList() : new List<int>();

        string name = "", os = "", model = "", vendor = "", serial = "", title = "";
        List<string> shares = new();
        string shareStatus = "", shareHost = "";

        if (ports.Any(p => p is 80 or 443 or 8080))
        {
            var fp = await HttpFingerprint.ProbeAsync(dial, ct).ConfigureAwait(false);
            title = fp.Title;
            vendor = fp.Vendor;
        }

        if (ports.Contains(443) && await TlsCertProbe.QueryAsync(dial, 443, ct).ConfigureAwait(false) is { } tls)
        {
            if (tls.Model.Length > 0) model = tls.Model;
            if (tls.Serial.Length > 0) serial = tls.Serial;
            if (vendor.Length == 0 && tls.Vendor.Length is > 0 and <= 30) vendor = tls.Vendor;
        }

        if (ports.Contains(445))
        {
            if (await SmbInfoProbe.QueryAsync(dial, 445, ct).ConfigureAwait(false) is { } smb)
            {
                name = smb.ComputerName;
                os = smb.OsFriendly;
            }

            if (OperatingSystem.IsWindows())
                try
                {
                    // ⚠️ Asked by the v6 literal only – deliberately NOT via ListForHostAsync, whose first
                    // candidate is the device's name. A name resolves to the IPv4 address on nearly every
                    // dual-stack host, so that path would answer with the v4 share list and label it v6.
                    var unc = SmbShares.UncHost(dial);
                    var listing = SmbShares.ListAsync(unc, ct);
                    // Raced: NetShareEnum is a blocking native call that ignores the token.
                    if (await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(5), ct))
                            .ConfigureAwait(false) == listing)
                    {
                        var result = await listing.ConfigureAwait(false);
                        shares = result.Shares;
                        shareHost = unc;
                        shareStatus = result.Status == ShareListStatus.AccessDenied && shares.Count == 0
                            ? "denied" : "";
                    }
                }
                catch { /* best effort – no shares is a normal answer */ }
        }

        if (community.Length > 0 &&
            await SnmpProbe.QueryAsync(dial, ct, community).ConfigureAwait(false) is { } snmp)
        {
            if (snmp.SysName.Length > 0 && name.Length == 0) name = snmp.SysName;
            if (snmp.SysDescr.Length > 0 && model.Length == 0) model = snmp.SysDescr;
        }

        return new Ipv6Facts(true, name, os, model, vendor, serial, title,
            shares, shareStatus, shareHost);
    }

    /// <summary>Guards against two probe passes running over the same live devices. The scan's enrichment
    /// and the background auto-refresh could both start one (the `!_scanning` check they used is a plain
    /// check-then-act), and they then mutated the same collections concurrently.</summary>
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    /// <param name="waitForTurn">Whether to queue behind a pass that is already running instead of skipping.
    /// The scan sets this: its probe pass is the one that fills in the model, the OS, the SMB shares and the
    /// vendor details, so silently doing nothing leaves the user looking at a half-identified list.
    /// The background refresh does not – if a pass is already under way, that refresh has nothing to add.</param>
    private async Task ProbeDevicesAsync(string community, CancellationToken ct, bool waitForTurn = false,
        Action<double>? onProgress = null)
    {
        // ⚠️ The scan used to drop its own probe pass here. The 30 s monitor takes this gate too, and its
        // check-then-act on _scanning could let it in a moment before the user pressed Scan – the scan then
        // found the gate taken and returned as if it had run. Result: a scan that found every device but
        // enriched none of them, at random.
        if (waitForTurn) await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        else if (!await _probeGate.WaitAsync(0, ct).ConfigureAwait(false)) return; // one already running

        try { await ProbeDevicesCoreAsync(community, ct, onProgress).ConfigureAwait(false); }
        finally { _probeGate.Release(); }
    }

    private async Task ProbeDevicesCoreAsync(string community, CancellationToken ct,
        Action<double>? onProgress = null)
    {
        List<Device> snapshot;
        lock (_lock) snapshot = _devices.ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(snapshot.Select(async d =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await ProbeOneDeviceAsync(d, community, ct).ConfigureAwait(false); }
            catch { /* best-effort per device */ }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                onProgress?.Invoke(snapshot.Count > 0 ? (double)n / snapshot.Count : 1);
            }
        })).ConfigureAwait(false);
        lock (_lock) Persist();
    }

    private async Task ProbeOneDeviceAsync(Device d, string community, CancellationToken ct)
    {
        // ⚠️ d is the LIVE device (this method writes to it), so every read of its collections has to be
        // under the lock too. Enumerating d.OpenPorts directly threw "Collection was modified" whenever a
        // second probe pass added port 53 – swallowed by the per-device catch, so the only symptom was
        // enrichment silently going missing.
        string host;
        List<int> ports;
        lock (_lock) { host = d.Host; ports = new List<int>(d.OpenPorts); }
        if (host.Length == 0) return;
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

        string hint;
        lock (_lock) hint = VendorHint(d); // reads ExtraInfo – same live dictionary the probes write
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

        // ⚠️ WMI first, and only on Windows. It is the only source that names the manufacturer, model and
        // BIOS serial of a Windows PC – SMB below gets the computer name and the OS build and nothing more.
        // Running it first is what gives it priority: SMB's writes are all gap-fillers ("if empty"), so
        // whatever WMI established stands. Without this, every Windows machine in the list showed a name and
        // an OS and three empty columns where the WPF client showed the hardware.
        if (ports.Contains(135) && OperatingSystem.IsWindows() && !ct.IsCancellationRequested)
        {
            try
            {
                var wmi = await WmiProbe.QueryAsync(host, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    foreach (var kv in wmi) d.ExtraInfo[kv.Key] = kv.Value;
                    // The BIOS serial belongs in the serial column, not in the details rows – but only when
                    // that column is still free. ⚠️ If an earlier probe (TLS certificate, Brother) already
                    // set a serial, removing the key as well would drop the BIOS serial entirely; it stays
                    // as a detail row instead, since two serials is a fact and not a conflict.
                    if (d.ExtraInfo.TryGetValue("Seriennummer", out var sn) && d.SerialNumber.Length == 0)
                    {
                        d.ExtraInfo.Remove("Seriennummer");
                        d.SerialNumber = sn;
                    }
                }
            }
            catch { /* no rights, RPC blocked, not a Windows host – SMB below still contributes */ }
        }

        if (ports.Contains(445))
        {
            if (await SmbInfoProbe.QueryAsync(host, 445, ct).ConfigureAwait(false) is { } smb)
                lock (_lock)
                {
                    if (smb.ComputerName.Length > 0 && d.Name.Length == 0) d.Name = smb.ComputerName;
                    if (smb.OsFriendly.Length > 0 && !d.ExtraInfo.ContainsKey("System")) d.ExtraInfo["System"] = smb.OsFriendly;
                }

            // Share names for the row-details shortcuts. ⚠️ Enumeration is a Windows API – guard explicitly
            // rather than relying on catch, so Linux/macOS never even reach it.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // ⚠️ By NAME first, address last (see ListForHostAsync). Asking the bare IP is what made
                    // shares silently never appear on real servers: they answer a name in seconds and take
                    // the better part of a minute to refuse an address.
                    string knownName;
                    lock (_lock) knownName = d.Name;
                    var shares = await SmbShares.ListForHostAsync(host, knownName, ct).ConfigureAwait(false);

                    lock (_lock)
                    {
                        _shareStatus[WebId(d)] = shares.Status;
                        if (shares.Shares.Count > 0)
                        {
                            d.Shares = shares.Shares.ToList();
                            // The form that worked – the UNC buttons must use it, not the address.
                            _shareHost[WebId(d)] = shares.Host;

                            // ⚠️ print$ is probed by name on purpose (a non-admin enumeration hides it) but
                            // was only ever turned into another share button. It is the driver store of a
                            // machine that shares printers, so record that as a fact.
                            // Deliberately NOT a device type: every Windows box with printer sharing on has
                            // this share, and calling those "printer" would fight the Windows signature rule
                            // that makes them PCs.
                            if (d.Shares.Any(s => s.Equals("print$", StringComparison.OrdinalIgnoreCase)))
                                d.ExtraInfo["Druckerfreigabe"] = "ja";
                        }
                    }
                }
                catch { /* best effort – no shares is a normal answer */ }
            }
        }

        // ⚠️ Skipped entirely when the community is empty – the user turned SNMP off. Worth an explicit
        // guard rather than passing "" along: SnmpProbe would still send the packet and still wait out its
        // timeout, so "off" would cost exactly as much as "on" while learning nothing.
        // Both SNMP versions are tried, so the badge can say which the device speaks (v1, v2c, or both) –
        // devices vary, and "SNMP works" is more useful split than merged. Same community for both.
        if (community.Length > 0)
        {
            // Both versions at once, not one after the other: a device with SNMP off would otherwise wait out
            // two 1.5 s timeouts back to back.
            var v1Task = SnmpProbe.QueryAsync(host, ct, community, version: 0);
            var v2cTask = SnmpProbe.QueryAsync(host, ct, community, version: 1);
            await Task.WhenAll(v1Task, v2cTask).ConfigureAwait(false);
            var snmpV1 = v1Task.Result;
            var snmpV2c = v2cTask.Result;
            var snmp = snmpV1 ?? snmpV2c;
            if (snmp is { } s)
                lock (_lock)
                {
                    if (s.SysName.Length > 0 && d.Name.Length == 0) d.Name = s.SysName;
                    if (s.SysDescr.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = s.SysDescr;

                    // Which versions answered → the SNMP badge(s). ServiceBadges reads this key (SNMP has no
                    // TCP port, so it can't come from the port scan the way http/ssh badges do).
                    var versions = new System.Collections.Generic.List<string>();
                    if (snmpV1 is not null) versions.Add("v1");
                    if (snmpV2c is not null) versions.Add("v2c");
                    if (versions.Count > 0) d.ExtraInfo["SNMP"] = string.Join(" ", versions);
                }
        }

        string encrypted, user;
        int sshPort;
        bool isZyxelSwitch, isTpLink, isZyxelFirewall;
        Device probeClone;
        lock (_lock) { encrypted = d.EncryptedPassword; user = d.Username.Trim(); sshPort = d.SshPort; isZyxelSwitch = IsZyxelSwitch(d); isTpLink = IsTpLink(d); isZyxelFirewall = IsZyxelFirewall(d); probeClone = d.Clone(); }
        if (ports.Contains(22) && encrypted.Length > 0 && user.Length > 0)
        {
            var password = CredentialProtector.Unprotect(encrypted);
            if (password.Length > 0)
            {
                if (isTpLink)
                {
                    // ⚠️ The dedicated TpLinkSshConnector, NOT the generic SshInfoProbe. TP-Link's own
                    // "show system-info" labels its firmware "Software Version", which the generic parser
                    // misses, and the connector also reads the hardware revision the firmware page needs.
                    // ⚠️ Port set to the SSH port: GetFactsAsync reads device.Port, and a TP-Link's Port is
                    // its HTTPS 443 – handing that to SSH would connect to the wrong port.
                    probeClone.Port = sshPort;
                    try
                    {
                        var f = await TpLinkSshConnector.GetFactsAsync(probeClone, password, ct).ConfigureAwait(false);
                        lock (_lock)
                        {
                            if (f.FirmwareVersion.Length > 0) d.ExtraInfo["Firmware"] = f.FirmwareVersion;
                            if (f.Model.Length > 0) d.ExtraInfo["Modell"] = f.Model;
                            if (f.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = f.Serial;
                            if (f.HardwareVersion.Length > 0) d.ExtraInfo["Hardware-Version"] = f.HardwareVersion;
                            // ⚠️ Only claim the OS once the switch actually answered (a model came back).
                            // "JetStream" is TP-Link's platform name for its managed switches – the honest
                            // analogue of "ZyNOS"/"RouterOS"; these boxes market no other OS name. Not set on
                            // a failed read, so an unreachable device doesn't get a fabricated OS.
                            if (f.Model.Length > 0) d.ExtraInfo["OS"] = "JetStream";
                        }
                    }
                    catch { /* best-effort: SSH off / wrong creds / not reachable */ }
                }
                else if (isZyxelSwitch)
                {
                    // ⚠️ The dedicated ZyxelSsh connector, NOT the generic SshInfoProbe. SshInfoProbe drives
                    // the SSH exec channel, which on this switch family's old sshd is fatal – on a GS1920 it
                    // closes the whole connection – so the generic probe returned nothing and the row's
                    // firmware/serial/OS stayed empty even after a login was set. ZyxelSsh reads over the
                    // interactive shell, which is the only transport these switches truly support.
                    var info = await ZyxelSsh.GetInfoAsync(host, sshPort, user, password, ct).ConfigureAwait(false);
                    if (info is { } zi)
                        lock (_lock)
                        {
                            // Authoritative, straight from the switch – so these overwrite rather than only
                            // filling a gap (a firmware string should refresh after an upgrade). ⚠️ Never
                            // Board: a filled Board is how IdentifiedVendor spots RouterOS, and setting it
                            // would relabel the switch "MikroTik" and reroute every later call.
                            if (zi.Firmware.Length > 0) d.ExtraInfo["Firmware"] = zi.Firmware;
                            if (zi.Model.Length > 0) d.ExtraInfo["Modell"] = zi.Model;
                            if (zi.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = zi.Serial;
                            d.ExtraInfo["OS"] = "ZyNOS";
                        }
                    // Serial fallback over SNMP: older ZyNOS omits it from the CLI (a GS1920 on V4.50), but
                    // the web GUI reads it from a Zyxel-private OID and so can TikMan. Best-effort.
                    bool needSerial;
                    lock (_lock) needSerial = d.SerialNumber.Length == 0;
                    if (needSerial && community.Length > 0)
                    {
                        var serial = await ZyxelSsh.GetSerialViaSnmpAsync(host, community, ct).ConfigureAwait(false);
                        if (serial.Length > 0) lock (_lock) { if (d.SerialNumber.Length == 0) d.SerialNumber = serial; }
                    }
                }
                else if (isZyxelFirewall)
                {
                    // ⚠️ ZLD, NOT ZyNOS: a Zyxel firewall has no "show system-information"; ZldSsh reads its own
                    // "show version" table (running image = model + firmware) and "show serial-number". Same
                    // Board caveat as the switch: never set it, or IdentifiedVendor would call this MikroTik.
                    var zfInfo = await ZldSsh.GetInfoAsync(host, sshPort, user, password, ct).ConfigureAwait(false);
                    if (zfInfo is { } zf)
                        lock (_lock)
                        {
                            if (zf.Firmware.Length > 0) d.ExtraInfo["Firmware"] = zf.Firmware;
                            if (zf.Model.Length > 0) d.ExtraInfo["Modell"] = zf.Model;
                            if (zf.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = zf.Serial;
                            d.ExtraInfo["OS"] = "ZLD";
                        }
                }
                else if (await SshInfoProbe.QueryAsync(host, sshPort, user, password, hint, ct).ConfigureAwait(false) is { } ssh)
                    lock (_lock)
                    {
                        if (ssh.Model.Length > 0 && !d.ExtraInfo.ContainsKey("Modell")) d.ExtraInfo["Modell"] = ssh.Model;
                        if (ssh.Serial.Length > 0 && d.SerialNumber.Length == 0) d.SerialNumber = ssh.Serial;
                        if (ssh.Firmware.Length > 0 && !d.ExtraInfo.ContainsKey("Firmware")) d.ExtraInfo["Firmware"] = ssh.Firmware;
                    }
            }
        }

        try
        {
            if (await DnsProbe.IsOpenAsync(host, ct).ConfigureAwait(false))
                lock (_lock) { if (!d.OpenPorts.Contains(53)) d.OpenPorts.Add(53); }
        }
        catch { /* best effort */ }

        // Now that vendor + model (+ any login) are established, fill the Latest column on its own – no need
        // to open the Update tab. Rate-limited per device so the 30-second background refresh doesn't turn it
        // into a round trip every half minute.
        await MaybeCheckLatestAsync(d).ConfigureAwait(false);
    }

    // ---- automatic "latest firmware" check during the meta phase ---------------------------------

    // WebId → when this device was last checked, so the auto-check fills the Latest column once on a scan
    // and then leaves it alone until it goes stale, instead of re-checking on every background refresh.
    private readonly Dictionary<string, long> _lastLatestCheck = new();
    // ⚠️ Two intervals. Once we have a real version, hold it for a good while (it rarely changes). But a
    // check that came back with NOTHING – a transient fetch failure, or the page didn't parse that time –
    // must NOT stick for half an hour: retry it in a couple of minutes so a one-off hiccup doesn't leave the
    // column reading "manual search" until the next full rescan. (A genuinely EOL/unparseable model just
    // keeps retrying cheaply; that is fine, it is one small GET.)
    private const long LatestOkIntervalMs = 30 * 60_000;   // 30 min after a successful parse
    private const long LatestRetryIntervalMs = 2 * 60_000; //  2 min while still unresolved

    /// <summary>Runs the update / latest-firmware check for a device the moment its model and vendor are
    /// known, if it's one we can check: MikroTik with a stored login (its own API), or a TP-Link / Zyxel
    /// switch with a known model (read off the vendor's web page, no login). Best-effort and rate-limited;
    /// anything else is skipped.</summary>
    private async Task MaybeCheckLatestAsync(Device d)
    {
        string id;
        lock (_lock)
        {
            id = WebId(d);
            var eligible = (IsMikroTik(d) && d.EncryptedPassword.Length > 0)
                        || ((IsTpLink(d) || IsZyxelSwitch(d)) && Model(d).Length > 0);
            if (!eligible) return;
            if (_lastLatestCheck.TryGetValue(id, out var last))
            {
                // Skip only if the previous check produced a real version; an empty/manual-search result
                // gets the short retry interval so it heals itself without waiting for a rescan.
                var haveVersion = _updates.TryGetValue(id, out var u) && u.Latest.Length > 0;
                var interval = haveVersion ? LatestOkIntervalMs : LatestRetryIntervalMs;
                if (Environment.TickCount64 - last < interval) return;
            }
            _lastLatestCheck[id] = Environment.TickCount64;
        }

        // ⚠️ null channel: read the device's CURRENT update channel (CheckForUpdatesAsync), never set one –
        // the auto-check must not write the channel behind the user's back. (SetChannelAsync is only for the
        // explicit channel dropdown.)
        try { await CheckAndRememberAsync(id).ConfigureAwait(false); }
        catch { /* best-effort: offline / no answer / rate-limited API */ }
    }

    // ---- topology (shared by the headless host and the GUI) --------------------------------------

    /// <summary>The flat logical map (Internet → devices) – instant, no forwarding tables needed.</summary>
    /// <summary>The address-distribution map. The local networks come from the adapters, so the blocks match
    /// the REAL prefix – a /21 is divided as a /21, not assumed to be a /24.</summary>
    public TopoLayout BuildLogicalTopology() =>
        PhysicalTopology.BuildLogical(TopoInputs(),
            NetworkInfo.GetLocalSubnets().Select(s => s.Cidr).ToList());

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

    /// <summary>The devices as the map builders want them.
    ///
    /// <para>⚠️ Vendor and model go in as two SEPARATE fields, each on its own label line. Joined into one
    /// they routinely exceed the box width, and since the model sits second it was the half the ellipsis
    /// ate – losing the more specific of the two facts.</para>
    ///
    /// <para>No de-duplication needed here: <see cref="Model"/> already strips a repeated maker off the
    /// front ("MikroTik CCR2004" → "CCR2004"), because the device list has the same two-column problem.
    /// Doing it a second time on the way to the map would be a copy of a rule that can then drift.</para></summary>
    private List<TopoInputDevice> TopoInputs() =>
        RawDevices().Select(d => new TopoInputDevice(
            WebId(d), d.Host, d.MacAddress, Display(d), d.Host, IsBridge(d),
            Vendor(d).Trim(), Model(d).Trim(), KindWithVm(d))).ToList();

    /// <summary>Devices that can hold a forwarding table – the ones worth asking for the physical map.
    /// <para>One definition, used by the map's inputs and by the gathering, so the two cannot disagree
    /// about which devices count as infrastructure.</para></summary>
    private static bool IsBridge(Device d) =>
        Kind(d) is DeviceKind.Switch or DeviceKind.Router or DeviceKind.AccessPoint or DeviceKind.Firewall
        || IsMikroTik(d);

    private async Task<(Dictionary<string, IReadOnlyDictionary<string, string>> Fdb,
                        Dictionary<(string, string), string> Ssids)> GatherFdbAsync()
    {
        string community;
        lock (_lock) community = SnmpCommunityLocked();
        var bridges = RawDevices().Where(IsBridge).ToList();

        var fdb = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var ssids = new Dictionary<(string, string), string>();
        // ⚠️ In parallel, and cached. Read one after another this asked every switch in turn and each one
        // costs a login – on an appliance that is seconds of handshake before any data moves, so a handful
        // of switches was most of a minute before the map appeared. They are independent reads.
        int parallel;
        lock (_lock) parallel = _appData.ParallelDeviceReads is >= 1 and <= 32 ? _appData.ParallelDeviceReads : 8;
        using var gate = new SemaphoreSlim(parallel);

        await Task.WhenAll(bridges.Select(async d =>
        {
            var id = WebId(d);
            // A table the re-read already fetched is used as-is: that is the whole point of collecting it
            // there. Only a device with no fresh entry is actually talked to.
            if (CachedFdb(id) is { } cached)
            {
                lock (fdb) fdb[id] = cached;
                return;
            }

            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var (table, wifi) = await OneFdbAsync(d, community).ConfigureAwait(false);
                if (table is { Count: > 0 })
                {
                    lock (fdb) fdb[id] = table;
                    StoreFdb(id, table);
                }
                if (wifi is not null)
                    lock (ssids) foreach (var (iface, ssid) in wifi) ssids[(id, iface)] = ssid;
            }
            catch { /* one unreachable switch must not cost us the whole map */ }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        return (fdb, ssids);
    }

    // ---- forwarding-table cache -------------------------------------------------------------------
    //
    // Reading a switch's forwarding table means logging in to it, and on the slower appliances the login
    // alone is several seconds. The targeted re-read already logs in to exactly these devices, so it grabs
    // the table while it is there and the map then builds from what is already in hand.
    //
    // ⚠️ Deliberately short-lived. A forwarding table is a snapshot of where devices were seen; a stale one
    // would draw a confidently wrong map. Past the age below the switch is asked again, and "rearrange"
    // clears the cache outright so the user always has a way to force fresh evidence.
    private readonly Dictionary<string, (IReadOnlyDictionary<string, string> Table, long At)> _fdbCache = new();

    private const long FdbCacheMs = 5 * 60 * 1000;

    private IReadOnlyDictionary<string, string>? CachedFdb(string id)
    {
        lock (_lock)
            return _fdbCache.TryGetValue(id, out var e) && Environment.TickCount64 - e.At < FdbCacheMs
                ? e.Table : null;
    }

    private void StoreFdb(string id, IReadOnlyDictionary<string, string> table)
    {
        lock (_lock) _fdbCache[id] = (table, Environment.TickCount64);
    }

    /// <summary>Drops the cached forwarding tables, so the next map build re-reads every switch. This is
    /// what "rearrange" does – the user asking for a fresh arrangement is also asking for fresh evidence.</summary>
    public void ClearFdbCache()
    {
        lock (_lock) _fdbCache.Clear();
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
                // ⚠️ REST first, then the SSH CLI – the same order every other read in this app uses, and
                // the reason the map was wrong. RouterOS devices very often have a broken HTTPS handshake
                // (a documented fact of this codebase, which is why monitoring, config export and the
                // update check all carry an SSH fallback). This one did not: the REST call threw, the catch
                // below turned it into "no table", SNMP is off on a stock RouterOS, and so EVERY MikroTik
                // silently contributed nothing. With only the TP-Link tables left, the map showed the four
                // TP-Link ports and filed the rest of the network under "path unknown" – which looked like
                // a broken merge, but the merge was fine; it had nothing from MikroTik to merge.
                try
                {
                    using var client = new RouterOsClient(d.Host, d.Port, d.UseHttps, d.Username, password, ignoreCertErrors: true);
                    raw = await client.GetBridgeHostsAsync().ConfigureAwait(false);
                    try { var neigh = await client.GetNeighborsAsync().ConfigureAwait(false); raw.AddRange(neigh.Select(n => (n.Mac, n.Interface))); }
                    catch { /* older RouterOS */ }
                    try { wifi = await client.GetWifiSsidsAsync().ConfigureAwait(false); }
                    catch { /* no wifi package */ }
                }
                catch { raw = null; }

                if (raw is null || raw.Count == 0)
                {
                    raw = await RouterOsSsh.GetBridgeHostsAsync(d.Host, d.SshPort, d.Username, password)
                        .ConfigureAwait(false);
                    if (raw is not null)
                    {
                        // The neighbour table names the PHYSICAL port a device is on, which is exactly what
                        // the map wants; merged in like the REST path does.
                        try
                        {
                            var neigh = await RouterOsSsh.GetNeighborsAsync(d.Host, d.SshPort, d.Username, password)
                                .ConfigureAwait(false);
                            if (neigh is not null) raw.AddRange(neigh);
                        }
                        catch { /* older RouterOS */ }
                    }
                    try
                    {
                        wifi ??= await RouterOsSsh.GetWifiSsidsAsync(d.Host, d.SshPort, d.Username, password)
                            .ConfigureAwait(false);
                    }
                    catch { /* no wifi package */ }
                }
            }
            else if (IsZyxelSwitch(d) && password.Length > 0)
            {
                var zy = await ZyxelSsh.GetFdbAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                raw = zy?.Select(kv => (kv.Key, kv.Value)).ToList();
            }
            else if (IsTpLink(d) && password.Length > 0)
            {
                // TP-Link JetStream: the forwarding table over the SSH CLI. These switches keep SNMP off by
                // default (measured: port 161 closed), so without this they contributed nothing to the map
                // and everything behind them hung off "path unknown".
                var tp = await TpLinkSshConnector.GetFdbAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                raw = tp?.Select(kv => (kv.Key, kv.Value)).ToList();
            }
            else if (IsZyxelFirewall(d) && password.Length > 0)
            {
                // ZLD firewall: its ARP table (show arp-table) maps a device's MAC to the firewall INTERFACE
                // it is on (lan1 / lan2 / dmz …). Not switch-port granular – a firewall is a gateway, not a
                // switch – but it segments devices by interface and anchors them under the gateway.
                var arp = await ZldSsh.GetFdbAsync(d.Host, d.SshPort, d.Username, password).ConfigureAwait(false);
                raw = arp?.Select(kv => (kv.Key, kv.Value)).ToList();
            }
        }
        catch { raw = null; }

        if (raw is null || raw.Count == 0)
        {
            // Same rule for the forwarding-table read: no community, no SNMP.
            try { if (community.Length > 0) { var snmp = await SnmpFdb.ReadAsync(d.Host, community).ConfigureAwait(false); raw = snmp?.Select(kv => (kv.Key, kv.Value)).ToList(); } }
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

    /// <summary>Keeps the list honest in the background. Reachability (a plain TCP connect) always runs, so
    /// the status column never goes stale. When <c>AutoRefreshEnabled</c> is on it additionally re-runs the
    /// per-device probes at the configured interval, so model/firmware/shares follow changes on the network
    /// instead of freezing at whatever the last scan happened to see.</summary>
    private async Task MonitorLoopAsync()
    {
        while (true)
        {
            // ⚠️ The whole body is guarded. This loop is fire-and-forget with no continuation, so anything
            // that escapes ends it for the lifetime of the process – and the status column would then keep
            // showing the last values forever, silently.
            int wait = 30;
            try { wait = await MonitorOnceAsync().ConfigureAwait(false); }
            catch { /* never let one bad cycle end the loop */ }

            // ⚠️ The pace is set out here, not at the end of the cycle. It used to be the last statement of
            // MonitorOnceAsync, which meant any throw – a probe, or just one subscriber blowing up inside
            // Changed – skipped it and the loop span at full speed, re-probing every device as fast as the
            // network allowed, forever. A failed cycle must still wait for the next one.
            await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
        }
    }

    // The heavier reads (resources, optional meta refresh) keep their own ~30 s cadence however fast the
    // alive check ticks – opening SSH sessions every five seconds would be abuse, one TCP connect is not.
    private long _lastHeavyMonitor;
    // The meta refresh (full re-probe) is paced separately from the resource read, so a fast monitoring
    // interval speeds up CPU/RAM without re-fingerprinting every device just as often.
    private long _lastMetaRefresh;

    /// <returns>How many seconds to wait before the next cycle.</returns>
    private async Task<int> MonitorOnceAsync()
    {
        bool aliveOn; int aliveSeconds; int monitorSecs;
        lock (_lock)
        {
            aliveOn = _appData.AliveCheckEnabled;
            aliveSeconds = _appData.AliveCheckSeconds is >= 5 and <= 3600 ? _appData.AliveCheckSeconds : 5;
            // The monitoring-tab dropdown offers 5/10/15/30/60/120; an out-of-range value from an older
            // file is clamped rather than trusted, so a hand-edited 0 can't spin the loop.
            monitorSecs = _appData.MonitorIntervalSeconds is >= 5 and <= 3600 ? _appData.MonitorIntervalSeconds : 30;
        }

        // Sleep just long enough to serve whichever is sooner: the alive check or the monitoring interval.
        int NextWait() => Math.Max(1, aliveOn ? Math.Min(aliveSeconds, monitorSecs) : monitorSecs);

        // ⚠️ The alive check is a setting now, and OFF means off: no background reachability traffic at
        // all. On, it is the cheapest thing the app does – one TCP connect per device to a port already
        // known to be open – which is why a 5-second default is affordable.
        if (aliveOn) await ProbeAllAsync().ConfigureAwait(false);

        // The alive check may tick every few seconds; the resource read runs at the user's monitoring
        // interval (default 30 s, as low as 5 s).
        if (Environment.TickCount64 - _lastHeavyMonitor < monitorSecs * 1000L) return NextWait();
        _lastHeavyMonitor = Environment.TickCount64;

        // ⚠️ The resource read is NOT part of the alive check. It opens an SSH/REST session to every
        // RouterOS device with a login – during an update run those are the devices that are rebooting,
        // which is what the suspension exists to leave alone.
        if (Volatile.Read(ref _refreshSuspended) == 0)
            await ReadResourcesAsync().ConfigureAwait(false);

        // ⚠️ The meta refresh keeps its own 30-second floor, independent of the monitoring interval. A user
        // who picks a 5 s CPU/RAM cadence wants fresher load numbers, NOT a full re-fingerprint of every
        // device every 5 seconds – that would re-open every web/SNMP/SSH probe and hammer the network.
        if (Environment.TickCount64 - _lastMetaRefresh < Math.Max(30_000L, monitorSecs * 1000L)) return NextWait();
        _lastMetaRefresh = Environment.TickCount64;

        bool autoRefresh; string community; bool simple;
        lock (_lock)
        {
            autoRefresh = _appData.AutoRefreshEnabled;
            community = SnmpCommunityLocked();
            simple = _appData.SimpleScanMode;
        }

        // Simple mode suppresses the probes here too – it is a promise about network chatter, and a
        // background refresh that ignored it would quietly break that promise.
        // ProbeDevicesAsync itself refuses to start a second concurrent pass, so the !_scanning check is
        // now just an optimisation rather than the only thing preventing an overlap.
        if (autoRefresh && !simple && !_scanning && Volatile.Read(ref _refreshSuspended) == 0)
        {
            try { await ProbeDevicesAsync(community, CancellationToken.None).ConfigureAwait(false); }
            catch { /* a failed refresh must never kill the loop */ }
            // A subscriber that throws must not cost us the pacing (see MonitorLoopAsync).
            try { Changed?.Invoke(); } catch { }
        }

        return NextWait();
    }

    /// <summary>Reads CPU / memory / uptime from every RouterOS device that has a stored login, so the
    /// monitoring columns mean something. Follows the same secure order as everything else: HTTPS REST first,
    /// SSH CLI when the TLS handshake is broken (which it often is on RouterOS), and never plain HTTP.
    /// <para>Failures are silent per device – an unreachable box simply keeps its previous reading rather
    /// than blanking the row or taking the loop down.</para></summary>
    /// <summary>Re-runs the per-device checks for just the given devices (by id: MAC, else host): TCP
    /// reachability, the gap-filling probes (SNMP/web/vendor), and – for RouterOS with a login – the
    /// resource read. This is what the context menu's "rescan" runs, and what follows saving credentials
    /// for a selection: the point of entering a login is that TikMan goes and uses it, immediately and for
    /// exactly those devices, not on the next background cycle.</summary>
    public async Task RescanDevicesAsync(IReadOnlyCollection<string> ids)
    {
        List<Device> targets; string community;
        lock (_lock)
        {
            var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            targets = _devices.Where(d => wanted.Contains(WebId(d)) || wanted.Contains(d.Host)).ToList();
            community = SnmpCommunityLocked();
        }
        if (targets.Count == 0) return;

        int parallel;
        lock (_lock) parallel = _appData.ParallelDeviceReads is >= 1 and <= 32 ? _appData.ParallelDeviceReads : 8;

        // Two units of work per device (probe, then the update check), so the bar reflects the wait rather
        // than jumping to 100 % while the slowest half is still running.
        BeginRescan(targets.Count * 2);
        try
        {
            // ⚠️ In parallel, not one after another. Every device costs at least one connection setup, and
            // on the slower appliances that alone is seconds – measured: a TP-Link switch spends ~8 s in the
            // SSH handshake before a single byte of data moves, and a plain `ssh` from the command line is
            // no faster, so it is the device and not our code. Run sequentially, re-reading a handful of
            // them added up to most of a minute of pure waiting. They are independent devices; the only
            // shared state is written under the lock, which ProbeOneDeviceAsync already does (the scan's
            // own probe pass has run it concurrently all along).
            using var gate = new SemaphoreSlim(parallel);
            await Task.WhenAll(targets.Select(async d =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    int port;
                    lock (_lock) port = d.OpenPorts.Count > 0 ? d.OpenPorts[0] : 0;
                    if (port > 0)
                    {
                        var up = await Reachability.TcpProbeAsync(d.Host, port).ConfigureAwait(false);
                        lock (_lock) _online[WebId(d)] = up;
                    }
                    try { await ProbeOneDeviceAsync(d, community, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* one stubborn device must not stop the rest of the selection */ }
                    StepRescan();

                    // The update check rides along. It needs exactly what was just established – a working
                    // login – and asking for it separately afterwards was a second trip to every device for
                    // a column the user had already asked to see filled.
                    bool canUpdate;
                    lock (_lock) canUpdate = IsMikroTik(d) && d.EncryptedPassword.Length > 0;
                    try { if (canUpdate) await CheckUpdateAsync(WebId(d)).ConfigureAwait(false); }
                    catch { /* no login, unreachable, not RouterOS – the column simply stays empty */ }

                    // ⚠️ Verify the stored login and remember the answer. Saving a password and having
                    // nothing happen is the worst of both worlds – the row looks configured, and every
                    // read quietly fails somewhere the user never sees. One authenticated round trip here
                    // turns that into a visible "this password is wrong".
                    bool hasLogin; string user, pw;
                    lock (_lock)
                    {
                        hasLogin = d.EncryptedPassword.Length > 0;
                        user = d.Username;
                        pw = hasLogin ? CredentialProtector.Unprotect(d.EncryptedPassword) : "";
                    }
                    if (hasLogin && pw.Length > 0)
                    {
                        try
                        {
                            var test = await TestLoginAsync(WebId(d), user, pw).ConfigureAwait(false);
                            // ⚠️ Only the message, never the password – this string reaches a tooltip.
                            lock (_lock) _loginFailure[WebId(d)] = test.Ok ? "" : test.Message;
                        }
                        catch (Exception ex) { lock (_lock) _loginFailure[WebId(d)] = ex.Message; }
                    }

                    // ⚠️ The forwarding table comes along for the ride. This is a switch we have just
                    // logged in to, and reading its table is the expensive half of building the physical
                    // map – doing it here means the map is drawn from evidence already in hand instead of
                    // starting a second round of logins to the same devices minutes later.
                    if (IsBridge(d))
                    {
                        try
                        {
                            var (table, _) = await OneFdbAsync(d, community).ConfigureAwait(false);
                            if (table is { Count: > 0 }) StoreFdb(WebId(d), table);
                        }
                        catch { /* the map falls back to reading it itself */ }
                    }
                    StepRescan();
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);

            await ReadResourcesAsync(targets).ConfigureAwait(false);
            lock (_lock) Persist();
        }
        finally { EndRescan(); }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Whether a targeted re-read is running, and how far along – its own state, separate from the
    /// scan's: the two can overlap (a login saved mid-scan starts one), and sharing the scan bar would make
    /// each look like the other had jumped.</summary>
    public bool Rescanning { get; private set; }
    public double RescanProgress { get; private set; }

    // ⚠️ One bar for all concurrent re-reads, not one per call. Saving credentials on a few MikroTiks and
    // then on a few TP-Links used to run the bar to the end, reset it, and run it again – which reads as
    // "it started over" when in truth there is simply more work now. The counters are shared, so a second
    // batch joining mid-flight lengthens the SAME bar, and it disappears when the last one finishes.
    private int _rescanRunners;
    private int _rescanTotal, _rescanDone;

    private void BeginRescan(int steps)
    {
        lock (_lock)
        {
            _rescanRunners++;
            _rescanTotal += steps;
        }
        PublishRescan();
    }

    private void StepRescan()
    {
        lock (_lock) _rescanDone++;
        PublishRescan();
    }

    private void EndRescan()
    {
        lock (_lock)
        {
            if (--_rescanRunners > 0) return;      // another batch is still going – keep the bar
            _rescanRunners = 0;
            _rescanTotal = _rescanDone = 0;
        }
        PublishRescan();
    }

    private void PublishRescan()
    {
        lock (_lock)
        {
            Rescanning = _rescanRunners > 0;
            RescanProgress = _rescanTotal > 0 ? Math.Clamp(_rescanDone / (double)_rescanTotal, 0, 1) : 0;
        }
        try { Changed?.Invoke(); } catch { }
    }

    private async Task ReadResourcesAsync(List<Device>? only = null)
    {
        // ⚠️ Not just MikroTik any more: Zyxel and TP-Link switches feed the CPU/memory/uptime columns and
        // the history chart too, each read through its own connector below.
        List<Device> targets;
        lock (_lock) targets = (only ?? _devices)
            .Where(d => d.EncryptedPassword.Length > 0 && (IsMikroTik(d) || IsZyxelSwitch(d) || IsTpLink(d) || IsZyxelFirewall(d)))
            .ToList();
        if (targets.Count == 0) return;

        foreach (var d in targets)
        {
            string host, user, encrypted; int sshPort; bool mt, zy, tp, fw;
            lock (_lock)
            {
                host = d.Host; user = d.Username; encrypted = d.EncryptedPassword; sshPort = d.SshPort;
                mt = IsMikroTik(d); zy = IsZyxelSwitch(d); tp = IsTpLink(d); fw = IsZyxelFirewall(d);
            }

            var password = CredentialProtector.Unprotect(encrypted);
            if (password.Length == 0) continue;

            ResourceInfo? res = null;
            try
            {
                res = mt ? await RouterOsSsh.GetResourceAsync(host, sshPort, user, password).ConfigureAwait(false)
                    : zy ? await ZyxelSsh.GetResourceAsync(host, sshPort, user, password).ConfigureAwait(false)
                    : tp ? await TpLinkSshConnector.GetResourceAsync(host, sshPort, user, password).ConfigureAwait(false)
                    : fw ? await ZldSsh.GetResourceAsync(host, sshPort, user, password).ConfigureAwait(false)
                    : null;
            }
            catch { /* unreachable / auth changed – keep the previous reading */ }

            if (res is null) continue;
            lock (_lock)
            {
                var key = WebId(d);
                _resources[key] = res;

                if (!_history.TryGetValue(key, out var points))
                    _history[key] = points = new List<ResourceSnapshot>();
                points.Add(new ResourceSnapshot
                {
                    Timestamp = DateTime.Now,
                    CpuLoad = res.CpuLoad,
                    MemoryUsedPercent = res.MemoryUsedPercent,
                });
                if (points.Count > HistoryPoints) points.RemoveRange(0, points.Count - HistoryPoints);
            }
        }
        Changed?.Invoke();
    }

    private async Task ProbeAllAsync(Action<double>? onProgress = null)
    {
        List<Device> snapshot;
        lock (_lock) snapshot = _devices.ToList();
        var changed = false;
        var done = 0;
        foreach (var d in snapshot)
        {
            // ⚠️ No open port means "we have no way to ask", not "it is down". Writing false here marked
            // every MNDP/ZON/mDNS-only device permanently Offline (red dot) while it was happily answering –
            // StatusText only reports Unknown for a *missing* entry, so the entry has to stay absent.
            int port;
            lock (_lock) port = d.OpenPorts.Count > 0 ? d.OpenPorts[0] : 0;
            if (port > 0)
            {
                bool up = await Reachability.TcpProbeAsync(d.Host, port).ConfigureAwait(false);
                lock (_lock)
                {
                    var key = WebId(d);
                    if (!_online.TryGetValue(key, out var prev) || prev != up) { _online[key] = up; changed = true; }
                }
            }
            // Counted for every device, port or not, so the bar reaches the end even in a list of
            // portless (discovery-only) devices.
            onProgress?.Invoke(snapshot.Count > 0 ? ++done / (double)snapshot.Count : 1);
        }
        // ⚠️ Notify only when a dot actually flipped. At a 5-second alive cadence an unconditional notify
        // rebuilt the whole device list every tick, which threw away the grid's multi-selection (and any
        // in-flight drag) although nothing on screen would change.
        if (changed) Changed?.Invoke();
    }

    /// <summary>Writes the fleet into the settings file.
    ///
    /// <para>⚠️ Deep copies. <c>_appData</c> is the same instance the GUI holds, and the GUI saves it from
    /// the UI thread without taking <c>_lock</c> – so parking live <see cref="Device"/> references here means
    /// the serialiser walks <c>ExtraInfo</c>/<c>OpenPorts</c> on one thread while a probe task mutates them
    /// on another, and the save dies with "Collection was modified" in the middle of writing the credential
    /// store. <c>ToList()</c> alone does not help: it copies the list, not the elements.</para></summary>
    private void Persist()
    {
        if (!_appData.PersistDeviceList) return;
        _appData.Devices = _devices.Select(d => d.Clone()).ToList();
        try { DeviceStore.Save(_appData); } catch { /* best effort */ }
    }

    // ---- classification helpers (public: the host's topology/backup gates share them) ------------

    public static string WebId(Device d) => d.MacAddress.Length > 0 ? d.MacAddress : d.Host;
    public static string Display(Device d) => d.Name.Length > 0 ? d.Name : d.Host;

    /// <summary>The vendor of the device itself.
    ///
    /// <para>Order of trust: what the device said about itself over an active probe, then what its model
    /// text implies, and only then the MAC OUI. ⚠️ The OUI names whoever made the network chip, which on a
    /// PC is Intel or Realtek and not the manufacturer at all – so it stays the last resort. Getting the
    /// order wrong makes a local scan contradict a VPN scan of the same device.</para></summary>
    public static string Vendor(Device d)
    {
        if (d.ExtraInfo.TryGetValue("Hersteller (Web)", out var w) && w.Length > 0) return w;

        // ⚠️ WMI's manufacturer. It was being collected and then never read: the probe filled "Hersteller"
        // and nothing in Core looked at that key, so every Windows PC showed its NIC's OUI ("Intel
        // Corporate") where the WPF client showed "Lenovo".
        if (d.ExtraInfo.TryGetValue("Hersteller", out var wmi) && Normalise(wmi) is { Length: > 0 } v) return v;

        // The brand as written in the model or the web title – path-independent, and after the explicit
        // claims above so it can never override one.
        var text = (d.ExtraInfo.TryGetValue("Modell", out var m) ? m + " " : "")
                 + (d.ExtraInfo.TryGetValue("Web-Titel", out var t) ? t : "");
        if (ModelVendor.FromModel(text) is { Length: > 0 } fromModel) return fromModel;

        var oui = OuiLookup.Lookup(d.MacAddress);
        var lower = oui.ToLowerInvariant();
        if (lower.Contains("mikrotik") || lower.Contains("routerboard")) return "MikroTik";
        return oui;
    }

    /// <summary>Tidies a WMI manufacturer string: they arrive shouted or with a legal suffix
    /// ("LENOVO", "Dell Inc."), and neither belongs in a table column.</summary>
    private static string Normalise(string vendor)
    {
        var v = vendor.Trim();
        if (v.Length == 0) return "";
        foreach (var suffix in new[] { " Inc.", " Inc", " Corporation", " Corp.", " Corp", " Co., Ltd.",
                                       " Co., Ltd", " Ltd.", " Ltd", " GmbH", " S.A.", " AG" })
            if (v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                v = v[..^suffix.Length].Trim();
                break;
            }
        // All-caps ("LENOVO") reads as shouting next to properly cased names; mixed case is left alone.
        return v == v.ToUpperInvariant() && v.Length > 1
            ? char.ToUpperInvariant(v[0]) + v[1..].ToLowerInvariant()
            : v;
    }

    /// <summary>The model to show and to classify on, folded from every source that carries one.
    ///
    /// <para>⚠️ WMI reports a machine in two halves and neither is the answer on its own:
    /// <c>Win32_ComputerSystem.Model</c> is the machine-type code ("20M9CTO1WW") while
    /// <c>Win32_ComputerSystemProduct.Version</c> is the name a human uses ("ThinkPad P52"). Showing only
    /// the first – which is what reading <c>ExtraInfo["Modell"]</c> alone did – turned every PC into a
    /// part number. Combined they read "ThinkPad P52 (20M9CTO1WW)", and the code is only appended when it
    /// actually adds something.</para>
    ///
    /// <para>The web title is the last resort: for a device that answers nothing else, the title of its
    /// admin page is often the only self-description there is. (This logic used to live in the WPF view
    /// model, which is why the other clients showed the bare code.)</para></summary>
    /// <summary>The device's own model string, exactly as a discovery reported it – a RouterOS board code,
    /// a Zyxel model, an SNMP sysDescr. What the classifier keys on.</summary>
    public static string Board(Device d) =>
        d.ExtraInfo.TryGetValue("Modell", out var m) ? m.Trim() : "";

    public static string Model(Device d)
    {
        d.ExtraInfo.TryGetValue("Produkt", out var product);
        d.ExtraInfo.TryGetValue("Modell", out var model);
        product = product?.Trim() ?? "";
        model = model?.Trim() ?? "";

        var text = product.Length > 0
            ? model.Length > 0 && !product.Contains(model, StringComparison.OrdinalIgnoreCase)
                ? $"{product} ({model})"
                : product
            : model.Length > 0 ? model
            : d.ExtraInfo.TryGetValue("Web-Titel", out var web) ? web.Trim()
            : "";

        // "Lenovo ThinkPad P52" next to a Vendor column already saying Lenovo is the same word twice.
        var vendor = Vendor(d);
        if (vendor.Length > 0 && text.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            text = text[vendor.Length..].TrimStart(' ', '-', ':', '/', '·', ',');
        return PrettyModel(text.Trim());
    }

    /// <summary>Tidies a discovery model that arrives as a lowercase, underscore-joined token
    /// ("usg_flex_500", a Zyxel ZON/UPnP style) into the way the vendor writes it ("USG FLEX 500"). A login
    /// read later gives the proper string anyway; this only makes the pre-login scan-only view read right.
    /// <para>⚠️ Deliberately generic – no per-model table – so a future "usg_flex_700" formats itself; and
    /// deliberately narrow – it fires ONLY on a pure lowercase_underscore token, so a real model string that
    /// is already spaced or mixed-case ("USG FLEX 500", "TL-SG2008", "ThinkPad P52") is returned untouched.</para></summary>
    public static string PrettyModel(string model)
    {
        var m = model ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(m, @"^[a-z][a-z0-9]*(_[a-z0-9]+)+$"))
            return m;
        return string.Join(' ', m.Split('_').Select(t => t.ToUpperInvariant()));
    }
    public static bool IsMikroTik(Device d) => Vendor(d) == "MikroTik";
    public static bool IsZyxelSwitch(Device d) =>
        Vendor(d).Contains("Zyxel", StringComparison.OrdinalIgnoreCase) && Kind(d) == DeviceKind.Switch;

    /// <summary>True for a Zyxel <b>ZLD firewall</b> (USG / ZyWALL / USG FLEX / ATP), which speaks the ZLD
    /// CLI – a different dialect from the ZyNOS switches (<see cref="IsZyxelSwitch"/>). No chicken-and-egg on
    /// the model: the firewall's web title alone ("USG FLEX 500") classifies it as a firewall (the
    /// usg/zywall/atp/… tokens), so this is true before any login, which is what lets the ZLD read run.</summary>
    public static bool IsZyxelFirewall(Device d) =>
        Vendor(d).Contains("Zyxel", StringComparison.OrdinalIgnoreCase) && Kind(d) == DeviceKind.Firewall;

    /// <summary>True for a TP-Link / Omada managed switch, which speaks the JetStream SSH CLI.
    /// <para>⚠️ Not gated on <see cref="DeviceKind.Switch"/> the way the Zyxel test is: an unmanaged-looking
    /// TP-Link box with no model yet still classifies as Unknown, and gating would then refuse to read the
    /// very device that would have told us what it is. The CLI itself is the check – it either answers or
    /// it does not.</para></summary>
    /// <summary>Whether TikMan may log in to this device at all – i.e. whether it knows <b>whose</b> CLI it
    /// would be talking to.
    ///
    /// <para>⚠️ Identified vendor only. An earlier version also accepted "port 22 is open", on the reasoning
    /// that a login is a door TikMan can knock on whoever built the box. That is the wrong trade: once
    /// credentials exist, TikMan starts sending that vendor's commands, and on an <b>unidentified</b> device
    /// nobody can say how they are read. Plenty of SMB gear answers SSH with a menu shell, where a command
    /// is not a command but a series of keystrokes picking menu entries. The upside of guessing is a filled
    /// column; the downside is unbounded and lands on someone else's hardware.</para>
    ///
    /// <para>The vendor is enough on its own and does <b>not</b> need the model – which matters, because a
    /// TP-Link switch reports no model until someone has logged in. The OUI names the maker long before
    /// that, and the maker is what decides which dialect gets spoken.</para></summary>
    /// <summary>The SNMP read community, or "" when the user has cleared it – which means <b>do not use
    /// SNMP at all</b>.
    ///
    /// <para>⚠️ An empty setting used to fall back to "public", so there was no way to switch SNMP off.
    /// That mattered more than it looks: SNMP is UDP, so a TCP port scan cannot find it, which means the
    /// only way to detect it is to send a real GET to <b>every</b> device and wait for the timeout. That is
    /// a defensible default – a read-only sysDescr/sysName GET is the most ordinary thing on a managed
    /// network – but it is traffic the user should be able to decline, and until now they could not.</para>
    ///
    /// <para>Caller must hold the lock.</para></summary>
    private string SnmpCommunityLocked()
    {
        var c = _appData.SnmpCommunity;
        // ⚠️ null (an old settings file that predates the field) still means "public"; a deliberately
        // emptied string means off. The two are different intents and must not collapse into one.
        if (c is null) return "public";
        return c.Trim();
    }

    public static bool CanUseCredentials(Device d) =>
        IsMikroTik(d) || IsTpLink(d) ||
        // Every Zyxel, not just the switches: the credentials dialog is also how a user reaches the
        // built-in terminal, and the per-OS connectors decide for themselves what they send.
        Vendor(d).Contains("Zyxel", StringComparison.OrdinalIgnoreCase);

    public static bool IsTpLink(Device d) =>
        Vendor(d).Contains("TP-Link", StringComparison.OrdinalIgnoreCase) ||
        Vendor(d).Contains("TPLink", StringComparison.OrdinalIgnoreCase) ||
        Vendor(d).Contains("Omada", StringComparison.OrdinalIgnoreCase);

    public static DeviceKind Kind(Device d)
    {
        if (d.ExtraInfo.TryGetValue("mDNS-Modell", out var mm) && mm.Length > 0
            && DeviceClassifier.MdnsModelKind(mm) is var mk && mk != DeviceKind.Unknown)
            return mk;
        // ⚠️ The RAW board, not the display model. Model() folds in the WMI product name and, failing
        // everything else, the web page title – feeding a page title to MikroTikKind as if it were a board
        // code would classify a MikroTik from whatever its login page happens to be called.
        if (Vendor(d) == "MikroTik" && Board(d) is { Length: > 0 } board)
            return DeviceClassifier.MikroTikKind(board);
        return DeviceClassifier.Guess(Vendor(d), d.OpenPorts, ClassifyText(d), d.Name);
    }

    private static string ClassifyText(Device d)
    {
        var title = d.ExtraInfo.TryGetValue("Web-Titel", out var t) ? t : "";
        return (Model(d) + " " + title).Trim();
    }

    public static int VncPort(Device d) => d.OpenPorts.Contains(5900) ? 5900 : d.OpenPorts.Contains(5901) ? 5901 : 0;

    /// <summary>Localised kind label, in the app's current language. Every <see cref="DeviceKind"/> needs a
    /// case – a missing one shows a blank type for a correctly classified device.
    /// <para>⚠️ Localised, not English: the labels used to be hard-coded English here, which is why the
    /// type column stayed English in the Avalonia client while the rest of the UI followed the language.
    /// The Dev_* strings already existed (the WPF client used them); this just reads them. Unknown stays
    /// "" – both the empty-list check and the search haystack rely on that.</para></summary>
    public static string KindText(DeviceKind k) => k switch
    {
        DeviceKind.Router => T("Dev_Router"),
        DeviceKind.Firewall => T("Dev_Firewall"),
        DeviceKind.Switch => T("Dev_Switch"),
        DeviceKind.AccessPoint => T("Dev_AccessPoint"),
        DeviceKind.Printer => T("Dev_Printer"),
        DeviceKind.Nas => T("Dev_Nas"),
        DeviceKind.Pc => T("Dev_Pc"),
        DeviceKind.Phone => T("Dev_Phone"),
        DeviceKind.Camera => T("Dev_Camera"),
        DeviceKind.IoT => T("Dev_IoT"),
        DeviceKind.Server => T("Dev_Server"),
        DeviceKind.Ups => T("Dev_Ups"),
        DeviceKind.Laptop => T("Dev_Laptop"),
        DeviceKind.Notebook => T("Dev_Notebook"),
        DeviceKind.Tablet => T("Dev_Tablet"),
        DeviceKind.PaymentTerminal => T("Dev_PaymentTerminal"),
        DeviceKind.Franking => T("Dev_Franking"),
        DeviceKind.TimeRecording => T("Dev_TimeRecording"),
        DeviceKind.Management => T("Dev_Management"),
        DeviceKind.Smartphone => T("Dev_Smartphone"),
        DeviceKind.Audio => T("Dev_Audio"),
        DeviceKind.GameConsole => T("Dev_GameConsole"),
        DeviceKind.Tv => T("Dev_Tv"),
        DeviceKind.StreamingBox => T("Dev_StreamingBox"),
        _ => "",
    };

    // ---- internals -------------------------------------------------------------------------------

    private Device? Find(string id) => _devices.FirstOrDefault(d => WebId(d) == id);

    private string StatusText(Device d) =>
        _online.TryGetValue(WebId(d), out var up) ? (up ? "Online" : "Offline")
            : _seenAlive.Contains(WebId(d)) ? "Answered"
            : "Unknown";

    // The machine's actual default gateways, cached briefly: IsGateway used to be derived from the device
    // TYPE (router/firewall ⇒ "gateway"), which painted every router in the list orange. Only the box this
    // machine actually routes through is the gateway. Cached because Snapshot() runs per refresh and asking
    // the OS for its gateways is not free; 30 s is fresher than gateways ever change.
    private HashSet<string>? _gateways;
    private long _gatewaysReadAt;

    private HashSet<string> Gateways()
    {
        if (_gateways is null || Environment.TickCount64 - _gatewaysReadAt > 30_000)
        {
            try { _gateways = NetworkInfo.GetDefaultGateways(); }
            catch { _gateways ??= new HashSet<string>(); }
            _gatewaysReadAt = Environment.TickCount64;
        }
        return _gateways;
    }

    /// <summary>The kind label with a "(VM)" tag when the MAC is out of a hypervisor's OUI range.
    ///
    /// <para>⚠️ Here, not in <see cref="KindText(DeviceKind)"/>: that one only has the enum, and the VM
    /// evidence is the MAC. A guest gets its NIC from the host's OUI block, so the MAC alone separates a
    /// virtual machine from bare metal – worth flagging, because a "Server" that is really a guest
    /// actually lives on some other box. A VM we cannot otherwise place is at least known to be one.
    /// (Lived in the WPF view model until now, so the other clients never showed it.)</para></summary>
    private string KindWithVm(Device d)
    {
        var kind = KindText(Kind(d));
        if (Virtualization.Hypervisor(d.MacAddress).Length == 0) return kind;
        return kind.Length > 0 ? $"{kind} (VM)" : "VM";
    }

    private DeviceSnapshot ToSnapshot(Device d)
    {
        // ⚠️ A v6-only neighbour (found via the neighbour cache, no IPv4) carries its IPv6 address in Host,
        // and Host is what fills the IPv4 column – so its v6 address showed up under "IPv4". Split it out:
        // an IPv6 Host is not an IPv4 address, and it belongs in the IPv6 list with the device's other v6
        // addresses, not in the v4 cell.
        var hostIsV6 = d.Host.Contains(':');
        var v4 = hostIsV6 ? "" : d.Host;
        var v6 = d.AltAddresses.Where(a => a.Contains(':'));
        if (hostIsV6) v6 = v6.Prepend(d.Host);

        var firmware = d.ExtraInfo.TryGetValue("Firmware", out var fwv) ? fwv
            : d.ExtraInfo.TryGetValue("Version", out var vrv) ? vrv : "";
        var latestVersion = UpdateOf(d)?.Latest ?? "";
        // MikroTik has no per-model download page but publishes a per-version changelog, so both release
        // numbers become links to their own notes: the offered version on the Latest cell (already the
        // stored URL for the web vendors), the installed version on the Version cell.
        var latestUrl = UpdateOf(d)?.LatestUrl is { Length: > 0 } stored ? stored
            : IsMikroTik(d) ? FirmwareChangelog.UrlFor("MikroTik", latestVersion) : "";
        var versionUrl = FirmwareChangelog.UrlFor(Vendor(d), firmware);

        return new(
        WebId(d), Display(d), v4, d.MacAddress, Vendor(d), Kind(d), KindWithVm(d), Model(d),
        StatusText(d), Gateways().Contains(d.Host), d.EncryptedPassword.Length > 0,
        VncPort(d), d.Username,
        // ⚠️ The ExtraInfo keys are German (they double as classifier lookup keys), so localise them here on
        // the way to the details pane – otherwise "Hersteller/Modell/Bauform/Produkt" show up even when the
        // UI language is English. Values are device data and stay as-is.
        d.ExtraInfo.Select(kv => new KeyValuePair<string, string>(InfoKeyLabels.Localize(kv.Key), kv.Value)).ToList(),
        v6.Distinct().ToList(),
        IsMikroTik(d) || IsZyxelSwitch(d) || IsTpLink(d) || IsZyxelFirewall(d), IsMikroTik(d), IsMikroTik(d), CanReadLogs(d),
        _loginFailure.TryGetValue(WebId(d), out var le) ? le : "",
        CanUseCredentials(d),
        d.SerialNumber,
        // ⚠️ "OS" before "System": WMI's caption ("Microsoft Windows 11 Pro") is the precise one, SMB's
        // FriendlyOs ("Windows 10/Server 2016+") is a build-number guess. The WMI key had no reader at all,
        // so the coarse answer was winning by default.
        d.ExtraInfo.TryGetValue("OS", out var wmiOs) && wmiOs.Length > 0 ? wmiOs
            : d.ExtraInfo.TryGetValue("System", out var os) && os.Length > 0 ? os
            // MikroTik hardware runs RouterOS – label the OS column so it reads like the other switch
            // vendors (ZyNOS, JetStream). ⚠️ Only once there is RouterOS evidence (a board or a version from
            // an actual read), not on a bare OUI guess, so an unprobed MikroTik doesn't claim an OS it might
            // not have (a CRS in SwOS mode). Board is set only by RouterOS (REST/MNDP/SSH resource).
            : IsMikroTik(d) && (Board(d).Length > 0 || d.ExtraInfo.ContainsKey("Firmware") || d.ExtraInfo.ContainsKey("Version"))
                ? "RouterOS" : "",
        firmware,
        d.Shares.ToList(),
        // Only interesting when there is nothing to show: "denied" explains an empty area, "" while the
        // buttons are there would just be noise under them.
        d.Shares.Count > 0 ? ""
            : _shareStatus.TryGetValue(WebId(d), out var ss) && ss == ShareListStatus.AccessDenied ? "denied"
            : "",
        _shareHost.TryGetValue(WebId(d), out var sh) && sh.Length > 0 ? sh : d.Host,
        ServiceBadges.For(d.Host, d.OpenPorts, d.ExtraInfo),
        d.SshPort, d.OpenPorts.ToList(),
        CpuText(d), MemoryText(d), UptimeText(d),
        // ⚠️ The OUI registrant, which is NOT the same as Vendor: that one prefers what the device said
        // about itself (web fingerprint, SNMP, mDNS) and only falls back to the OUI. They differ exactly
        // where it is interesting – an ODM-built box reports its brand but carries the ODM's MAC block.
        OuiLookup.Lookup(d.MacAddress),
        latestVersion, UpdateOf(d)?.InstalledDate ?? "",
        UpdateOf(d)?.LatestDate ?? "", UpdateOf(d)?.Available ?? false,
        latestUrl, versionUrl,
        Ipv6PortsOf(d), Ipv6MetaOf(d));
    }

    /// <summary>The interface index to use as the IPv6 zone for link-local addresses – the adapter the scan
    /// is bound to, or the one carrying the default route when "all networks" is chosen.
    ///
    /// <para>Returns 0 when there is no usable IPv6 interface, which makes the probe skip link-locals rather
    /// than dial them on a guessed link.</para></summary>
    private long Ipv6ScopeId()
    {
        try
        {
            var host = SelectedHostAddress();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                if (!props.UnicastAddresses.Any(u =>
                        u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
                    continue;

                // Bound to one adapter: use the one that owns that IPv4 address.
                if (host.Length > 0 &&
                    !props.UnicastAddresses.Any(u => u.Address.ToString() == host)) continue;

                try { return props.GetIPv6Properties().Index; }
                catch (NetworkInformationException) { /* adapter without IPv6 properties */ }
            }
        }
        catch (NetworkInformationException) { /* no usable interface – link-locals stay unprobed */ }
        return 0;
    }

    /// <summary>What each of this device's IPv6 addresses answered on, for the addresses that were probed.
    /// An address missing from the dictionary was never tested – which the UI shows differently from
    /// "tested and silent".</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<int>> Ipv6PortsOf(Device d)
    {
        var result = new Dictionary<string, IReadOnlyList<int>>();
        foreach (var a in Ipv6AddressesOf(d))
            if (_ipv6Ports.TryGetValue(a, out var ports)) result[a] = ports;
        return result;
    }

    /// <summary>Per-address enrichment results for this device's IPv6 addresses. Same shape and same reason
    /// as <see cref="Ipv6PortsOf"/>: a missing address means "not asked".</summary>
    private IReadOnlyDictionary<string, Ipv6Facts> Ipv6MetaOf(Device d)
    {
        var result = new Dictionary<string, Ipv6Facts>();
        foreach (var a in Ipv6AddressesOf(d))
            if (_ipv6Meta.TryGetValue(a, out var facts)) result[a] = facts;
        return result;
    }

    /// <summary>Every IPv6 address of a device, as the IPv6 view lists them.
    /// <para>⚠️ Includes <see cref="Device.Host"/> when that is itself a v6 address: a v6-only neighbour
    /// (found through the neighbour cache, no IPv4 at all) carries its only address there and has nothing
    /// in <c>AltAddresses</c> – reading just the alternates left exactly those devices unprobed, which is
    /// the one case where the IPv6 pass is not optional.</para></summary>
    private static IEnumerable<string> Ipv6AddressesOf(Device d)
    {
        if (d.Host.Contains(':')) yield return d.Host;
        foreach (var a in d.AltAddresses)
            if (a.Contains(':') && a != d.Host) yield return a;
    }

    private UpdateState? UpdateOf(Device d) =>
        _updates.TryGetValue(WebId(d), out var u) ? u : null;

    // Monitoring columns. Empty (not "0%") when nothing has been read yet – a device without a login is
    // never polled, and showing zero would read as "idle" rather than "unknown".
    private string CpuText(Device d) =>
        _resources.TryGetValue(WebId(d), out var r) ? $"{r.CpuLoad}%" : "";

    private string MemoryText(Device d)
    {
        if (!_resources.TryGetValue(WebId(d), out var r)) return "";
        // Byte totals (MikroTik, Zyxel) → "19% (5 MiB/29 MiB)"; a percentage only (TP-Link) → "19%".
        if (r.TotalMemory > 0)
            return $"{r.MemoryUsedPercent:0}% ({Mib(r.TotalMemory - r.FreeMemory)}/{Mib(r.TotalMemory)})";
        return r.MemoryPercent is { } p ? $"{p:0}%" : "";
    }

    private string UptimeText(Device d) =>
        _resources.TryGetValue(WebId(d), out var r) ? r.Uptime : "";

    private static string Mib(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GiB" : $"{bytes / 1024 / 1024} MiB";

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
