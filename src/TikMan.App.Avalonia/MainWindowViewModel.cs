using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Threading;
using TikMan.Core;
using TikMan.Core.Api;
using TikMan.Core.Diagnostics;
using TikMan.Core.Discovery;
using TikMan.Core.Fleet;
using TikMan.Core.Storage;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

/// <summary>The device-list view model: a thin binding layer over the shared <see cref="FleetService"/>.
/// The service does all the work (scan/enrich/classify/reachability) UI-free in Core; this just mirrors
/// its snapshots into an observable collection, marshals its change events onto the UI thread, and keeps
/// the selected device stable across the periodic refreshes.</summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly FleetService _fleet;
    private readonly TikMan.Core.Storage.AppData _appData;

    /// <summary>The live settings the fleet reads (SNMP community, ping timeout …). The settings dialog
    /// edits this instance; the fleet picks the values up on the next scan.</summary>
    public TikMan.Core.Storage.AppData Settings => _appData;
    public void SaveSettings() => DeviceStore.Save(_appData);

    /// <summary>The shared engine – the backup/update assistants call its per-device operations directly.</summary>
    public FleetService Fleet => _fleet;

    /// <summary>The current device snapshots (for the assistants to build their grids from).</summary>
    public IReadOnlyList<DeviceSnapshot> DeviceSnapshots => _fleet.Snapshot();

    public ObservableCollection<DeviceSnapshot> Devices { get; } = new();

    /// <summary>Just the devices that actually have IPv6 addresses – the dedicated IPv6 view binds to this
    /// so it stays a short, useful list instead of the full inventory with mostly empty rows.</summary>
    public ObservableCollection<DeviceSnapshot> Ipv6Devices { get; } = new();

    /// <summary>The IPv6 view's rows: one per address, not one per device – see <see cref="Ipv6Row"/> for
    /// why that distinction is the whole point of the tab.</summary>
    public ObservableCollection<Ipv6Row> Ipv6Rows { get; } = new();

    /// <summary>Whether the IPv6 tab is shown (setting). Re-read via <see cref="SettingsChanged"/> after the
    /// settings dialog closes, since that dialog edits the AppData instance directly.</summary>
    public bool ShowIpv6View => _appData.ShowIpv6View;

    /// <summary>Whether the "how to use the list" hint is shown (setting).</summary>
    public bool ShowListInfo => _appData.ShowListInfo;

    /// <summary>Whether the report / request / coffee row is shown (setting). Hiding it is for people who
    /// have already found those once and would rather have the space.</summary>
    public bool ShowContactButtons => _appData.ShowContactButtons;

    /// <summary>Whether the log view refetches on a timer. Persisted – it is a per-user habit, not a
    /// per-session one.</summary>
    public bool LogAutoRefresh
    {
        get => _appData.LogAutoRefresh;
        set { _appData.LogAutoRefresh = value; SaveSettings(); Raise(nameof(LogAutoRefresh)); }
    }

    /// <summary>Auto-refresh interval for the log, in seconds. Persisted.</summary>
    public int LogRefreshSeconds
    {
        get => _appData.LogRefreshSeconds > 0 ? _appData.LogRefreshSeconds : 5;
        set { _appData.LogRefreshSeconds = value; SaveSettings(); Raise(nameof(LogRefreshSeconds)); }
    }

    /// <summary>Default number of log entries to fetch. Persisted; 0 means "all".</summary>
    public int LogRowCap
    {
        get => _appData.LogRowCap;
        set { _appData.LogRowCap = value; SaveSettings(); Raise(nameof(LogRowCap)); }
    }

    /// <summary>How often the monitoring tab re-reads CPU/RAM/uptime, in seconds. Persisted; drives the
    /// background monitor's resource-read cadence (5/10/15/30/60/120).</summary>
    public int MonitorIntervalSeconds
    {
        get => _appData.MonitorIntervalSeconds > 0 ? _appData.MonitorIntervalSeconds : 30;
        set { _appData.MonitorIntervalSeconds = value; SaveSettings(); Raise(nameof(MonitorIntervalSeconds)); }
    }
    public bool HasIpv6Devices => Ipv6Devices.Count > 0;

    /// <summary>Whether this row has a real IPv4 address – the entry ticket to the IPv4 tab.
    /// <para>⚠️ Parsed, not "contains a dot": an IPv4-mapped v6 address (<c>::ffff:192.0.2.1</c>) contains
    /// dots and is still not an IPv4 address, and a hostname can contain them too.</para></summary>
    private static bool HasIpv4Address(DeviceSnapshot d) =>
        System.Net.IPAddress.TryParse(d.Ip, out var a) &&
        a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary>Call after the settings dialog closed so view-affecting settings take effect immediately.</summary>
    public void SettingsChanged()
    {
        ActionLog.Enabled = !_appData.DisableActionLog;
        Raise(nameof(ShowIpv6View));
        Raise(nameof(ShowListInfo));
        Raise(nameof(ShowContactButtons));
        // ⚠️ Rebuild the rows too. The device type ("Router" / "Drucker" …) is a localised string baked
        // into each snapshot, not a {loc:Loc} binding – so a language change from the settings dialog
        // would otherwise leave the whole Type column in the old language until the next scan.
        Refresh();
    }

    private string _status = T("Av_StatusReady");
    public string Status { get => _status; private set { _status = value; Raise(nameof(Status)); } }

    private bool _canScan = true;
    public bool CanScan
    {
        get => _canScan;
        private set
        {
            _canScan = value;
            Raise(nameof(CanScan)); Raise(nameof(ScanButtonText));
        }
    }

    // ---- scan progress (shown only while scanning) ------------------------------------------------

    private double _scanProgress;
    public double ScanProgress { get => _scanProgress; private set { _scanProgress = value; Raise(nameof(ScanProgress)); Raise(nameof(ScanProgressBrush)); } }

    /// <summary>True before the scan knows how many hosts it will touch – then the bar pulses instead of
    /// pretending to a percentage it doesn't have yet.</summary>
    private bool _scanIndeterminate;
    public bool ScanIndeterminate { get => _scanIndeterminate; private set { _scanIndeterminate = value; Raise(nameof(ScanIndeterminate)); } }

    /// <summary>Whether the progress block is shown at all – only while a scan is running; when it ends
    /// the whole block disappears rather than sitting there at 100 %.
    /// <para>There is no longer a choice of presentation: the bar and the protocol row are one display now
    /// (the bar runs 0 → 100 % over both stages and the row says which protocols are still listening), so
    /// the old "combined bar" setting had nothing left to switch between.</para>
    /// <para>⚠️ Tied to the SCAN alone, not to <see cref="CanScan"/>: CanScan now also goes false during a
    /// credential re-read (so the button can stop it), but that re-read has its own bar – showing the main
    /// scan block for it too would leave an empty grey bar standing next to the rescan bar.</para></summary>
    private bool _showProgress;
    public bool ShowProgress { get => _showProgress; private set { if (_showProgress == value) return; _showProgress = value; Raise(nameof(ShowProgress)); } }

    // ---- targeted re-read (after saving credentials, or "rescan devices") --------------------------
    //
    // ⚠️ Its own bar, under the scan's. The re-read runs on a handful of devices and is usually triggered
    // by an action the user just took (saving a login), so it needs to be visibly in progress – but it can
    // also overlap a running scan, and sharing one bar would make each look like the other had jumped.

    private bool _rescanRunning;
    public bool RescanRunning { get => _rescanRunning; private set { _rescanRunning = value; Raise(nameof(RescanRunning)); } }

    private double _rescanProgress;
    public double RescanProgress { get => _rescanProgress; private set { _rescanProgress = value; Raise(nameof(RescanProgress)); } }

    /// <summary>The scan's phases for the per-phase display, already turned into something a template can
    /// bind to (localised label, state glyph, colour).</summary>
    public ObservableCollection<PhaseVm> Phases { get; } = new();

    /// <summary>The headline phase, which supplies the bar's LABEL only (the bar itself binds the combined
    /// progress). One row for both stages: while discovery runs it reads "Discovery", and the moment the
    /// meta collection starts the same row reads "Meta data retrieval". Two rows for one continuous bar
    /// would have been one row too many for the same story.</summary>
    public PhaseVm? MainPhase { get; private set; }

    private void RefreshPhases()
    {
        var raw = _fleet.Phases;

        var protocols = new List<FleetService.ScanPhase>(raw.Count);
        FleetService.ScanPhase? scanPh = null, probePh = null;
        foreach (var ph in raw)
        {
            if (ph.Name == "scan") { scanPh = ph; continue; }
            if (ph.Name == "probe") { probePh = ph; continue; }
            protocols.Add(ph);
        }
        var merged = probePh is { State: FleetService.PhaseState.Running or FleetService.PhaseState.Done }
            ? probePh : scanPh;

        // ⚠️ Updated in place, never replaced. The item instances carry the pulse animation of the running
        // dot, and this refresh fires four times a second during a scan – a recreated item restarts the
        // animation each time, and an animation that restarts every quarter second never visibly pulses.
        if (merged is not null)
        {
            if (MainPhase is null) { MainPhase = new PhaseVm(merged); Raise(nameof(MainPhase)); }
            else MainPhase.Update(merged);
        }

        while (Phases.Count > protocols.Count) Phases.RemoveAt(Phases.Count - 1);
        for (var i = 0; i < protocols.Count; i++)
        {
            if (i < Phases.Count) Phases[i].Update(protocols[i]);
            else Phases.Add(new PhaseVm(protocols[i]));
        }
    }

    /// <summary>Bar colour in three stages along the one bar: red while the discovery protocols
    /// (MNDP/mDNS/SSDP/ZON) are still listening, sunflower yellow for the rest of the discovery stretch
    /// (protocols closed, sweep still running) up to 75 %, green from the meta phase to 100 %.</summary>
    private bool _discoveryDone;
    private bool _protocolsDone;
    public IBrush ScanProgressBrush =>
        new SolidColorBrush(Color.Parse(
            _discoveryDone ? "#4CAF50" : _protocolsDone ? "#FFC512" : "#E55353"));

    /// <summary>Adapter / subnet picker entries: "Alle lokalen Netze" plus one per up interface (its real
    /// CIDR). Choosing one fills <see cref="ScanRange"/>, which the scan actually uses (also editable).</summary>
    public ObservableCollection<ScanChoice> Adapters { get; } = new();

    private ScanChoice? _selectedAdapter;
    public ScanChoice? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            _selectedAdapter = value;
            Raise(nameof(SelectedAdapter));
            Raise(nameof(CanEditRange));
            Raise(nameof(RangeHint));
            if (value is not null) ScanRange = value.Cidr;
        }
    }

    /// <summary>False for "all local networks": the range box would be meaningless there, so it is greyed
    /// out rather than silently ignored.</summary>
    public bool CanEditRange => _selectedAdapter is { Cidr.Length: > 0 };

    /// <summary>Placeholder for the range box – empty for "all local networks", where an example CIDR would
    /// only suggest the greyed-out field still means something.</summary>
    public string RangeHint => CanEditRange ? T("Av_ScanRangeHint") : "";

    private string _scanRange = "";
    public string ScanRange { get => _scanRange; set { _scanRange = value; Raise(nameof(ScanRange)); } }

    /// <summary>Free-text filter over the visible device columns; empty shows everything.</summary>
    private string _filter = "";
    private DispatcherTimer? _filterDebounce;
    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Raise(nameof(Filter));

            // ⚠️ Debounced. Refresh() rebuilds both device collections and every IPv6 row from a fresh
            // snapshot; doing that on each keystroke made a five-letter word rebuild the list five times
            // while the user was still typing it. A quarter of a second is below the threshold where the
            // list feels lazy, and long enough that ordinary typing produces one rebuild.
            _filterDebounce?.Stop();
            _filterDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _filterDebounce.Tick -= OnFilterDue;
            _filterDebounce.Tick += OnFilterDue;
            _filterDebounce.Start();
        }
    }

    private void OnFilterDue(object? sender, EventArgs e)
    {
        _filterDebounce?.Stop();
        Refresh();
    }

    private bool Matches(DeviceSnapshot d)
    {
        if (_filter.Trim().Length == 0) return true;
        // Every entered word must appear somewhere in the row – same rule as the WPF filter.
        // Everything the row displays is searchable, the protocol tags ("http", "ssh", "rtsp") included –
        // filtering for "rtsp" to find the cameras is the obvious thing to want and used to find nothing.
        // Also the MAC vendor, the collected ExtraInfo facts, and the alt (IPv6) addresses.
        var tags = string.Join(" ", d.Badges.Select(b => b.Name));
        var info = string.Join(" ", d.Info.Select(kv => $"{kv.Key} {kv.Value}"));
        var ipv6 = string.Join(" ", d.Ipv6);
        var haystack = $"{d.Name} {d.Ip} {d.Mac} {d.Vendor} {d.MacVendor} {d.Model} {d.KindText} {d.Status} " +
                       $"{d.Serial} {d.Os} {d.Firmware} {d.Ipv6Summary} {ipv6} {tags} {info}";
        return _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The gentle "sign in for full quality" hint – shown until any device has credentials.</summary>
    public bool ShowNoLoginBanner => !_fleet.Snapshot().Any(d => d.HasLogin);

    /// <summary>Keeps re-running the scan until switched off (a slow-changing network still gets noticed).</summary>
    private bool _restartQueued;
    private bool _continuousScan;
    public bool ContinuousScan
    {
        get => _continuousScan;
        set { _continuousScan = value; Raise(nameof(ContinuousScan)); if (value) Scan(); }
    }

    /// <summary>Periodically re-reads the devices (reachability already runs; this refreshes the view).</summary>
    public bool AutoRefresh
    {
        get => _appData.AutoRefreshEnabled;
        // Persist right away: these live on the toolbar, not in the settings dialog, so nothing else would
        // ever write them out and the toggle silently reset on the next start.
        set { _appData.AutoRefreshEnabled = value; Raise(nameof(AutoRefresh)); SaveSettings(); }
    }

    /// <summary>Address scan only – no MNDP/ZON/mDNS/UPnP packets and no SNMP/WMI/web probes.</summary>
    public bool SimpleScanMode
    {
        get => _appData.SimpleScanMode;
        set
        {
            _appData.SimpleScanMode = value;
            Raise(nameof(SimpleScanMode));
            SaveSettings();

            // ⚠️ Switching the columns back on does not bring data back that was never gathered. Say so:
            // otherwise a full-mode list after a simple scan looks broken rather than simply un-scanned.
            // Only when it would actually be empty – if a full scan ran earlier, the values are there.
            if (!value && _fleet.Snapshot().Any(d => d.Model.Length == 0 && d.KindText.Length == 0))
                ActionResult = T("Av_SimpleModeNeedsScan");
        }
    }

    /// <summary>The continuous alive check: on by default, every 5 seconds. Off means genuinely off – the
    /// fleet's monitor loop then runs no background reachability probing at all, and the traffic lights
    /// keep whatever the last scan or rescan established.</summary>
    public bool AliveCheck
    {
        get => _appData.AliveCheckEnabled;
        set { _appData.AliveCheckEnabled = value; SaveSettings(); Raise(nameof(AliveCheck)); }
    }

    public int AliveCheckSeconds
    {
        get => AliveChoices.Contains(_appData.AliveCheckSeconds) ? _appData.AliveCheckSeconds : 5;
        set
        {
            if (!AliveChoices.Contains(value)) return;
            _appData.AliveCheckSeconds = value;
            SaveSettings();
            Raise(nameof(AliveCheckSeconds));
        }
    }

    public IReadOnlyList<int> AliveChoices { get; } = new[] { 5, 10, 20, 30, 60 };

    /// <summary>How many devices the inventory holds – for the "this removes N devices" confirmation.</summary>
    public int DeviceCount => _fleet.Snapshot().Count;

    /// <summary>Clears the whole inventory (the list, not the network). ⚠️ The caller must have confirmed:
    /// with a persisted list this also destroys every stored login.</summary>
    public void ClearList()
    {
        foreach (var d in _fleet.Snapshot()) _fleet.RemoveDevice(d.Id);
        SelectedDevice = null;
        ActionResult = T("Av_ListCleared");
    }

    private DeviceSnapshot? _selected;
    public DeviceSnapshot? SelectedDevice
    {
        get => _selected;
        set
        {
            _selected = value;
            Raise(nameof(SelectedDevice));
            Raise(nameof(HasSelection));
            Raise(nameof(CanSetCredentials));
            Raise(nameof(ShowDeviceDetail));
            Raise(nameof(HasDetailFacts));
            Raise(nameof(CanReadLogs));
            Raise(nameof(MonitoringHint));
            Raise(nameof(CanWake));
            Raise(nameof(CanBackup));
            Raise(nameof(CanVnc));
            Raise(nameof(CanSsh));
            Raise(nameof(CanSftp));
            Raise(nameof(CanRdp));
            Raise(nameof(CanTelnet));
            Raise(nameof(CanOpenFirmware));
            Raise(nameof(HasMonitoring));
        }
    }

    /// <summary>Loads the selected device's RouterOS log (SSH round trip). Empty list when it has no login
    /// or does not answer – the caller shows that as an empty table rather than an error dialog.</summary>
    /// <param name="maxEntries">Row cap; 0 fetches the lot. The full log can be large, so the caller's
    /// choice is passed straight through to what crosses the wire.</param>
    public async Task<IReadOnlyList<TikMan.Core.Models.LogEntry>> LoadLogsAsync(int maxEntries = 200)
    {
        if (_selected is null) return Array.Empty<TikMan.Core.Models.LogEntry>();
        // ⚠️ The fetch is an SSH round trip that takes a few seconds – on a switch it opens a shell, reads,
        // and cleans the output – and until now the grid just sat empty with no sign anything was happening.
        // LogLoading drives a centred "reading the log…" overlay so the wait reads as work, not a hang.
        LogLoading = true;
        try
        {
            var logs = await _fleet.LoadLogsAsync(_selected.Id, maxEntries);
            if (logs is null) { ActionResult = T("Av_LogUnavailable"); return Array.Empty<TikMan.Core.Models.LogEntry>(); }
            return logs;
        }
        finally { LogLoading = false; }
    }

    private bool _logLoading;
    /// <summary>True while the log is being fetched over SSH, so the view can show a "working on it" notice.</summary>
    public bool LogLoading { get => _logLoading; private set { _logLoading = value; Raise(nameof(LogLoading)); } }

    /// <summary>Checks the given devices for updates and fills the list's update columns. Sequential on
    /// purpose: each check is an authenticated round trip, and hammering a dozen routers at once to fill
    /// three columns is not a trade worth making.</summary>
    public async Task CheckUpdatesAsync(IReadOnlyList<string> deviceIds)
    {
        ActionResult = T("Av_CheckingUpdates", deviceIds.Count);
        ActionLog.Write("updates.check", $"devices={deviceIds.Count}");

        var withUpdate = 0;
        foreach (var id in deviceIds)
        {
            var info = await _fleet.CheckAndRememberAsync(id, _fleet.UpdateChannelOf(id));
            if (info?.UpdateAvailable == true) withUpdate++;
        }
        ActionResult = T("Av_CheckedUpdates", deviceIds.Count, withUpdate);
    }

    /// <summary>CPU/RAM history for the selected device, for the monitoring chart.</summary>
    public IReadOnlyList<TikMan.Core.Models.ResourceSnapshot> SelectedHistory =>
        _selected is null
            ? Array.Empty<TikMan.Core.Models.ResourceSnapshot>()
            : _fleet.HistoryOf(_selected.Id);

    /// <summary>Every device the user has marked in the grid. Bulk actions work off this; it holds the one
    /// selected row in the ordinary case, so callers never need to special-case single selection.
    /// <para>Kept in step by the window's SelectionChanged handler – Avalonia's DataGrid.SelectedItems is a
    /// plain property, not a bindable one.</para></summary>
    private IReadOnlyList<DeviceSnapshot> _marked = Array.Empty<DeviceSnapshot>();
    public IReadOnlyList<DeviceSnapshot> Marked =>
        _marked.Count > 0 ? _marked : (_selected is null ? Array.Empty<DeviceSnapshot>() : new[] { _selected });

    public void SetMarked(IEnumerable<DeviceSnapshot> devices)
    {
        _marked = devices.Distinct().ToList();
        Raise(nameof(Marked));
        Raise(nameof(MarkedCount));
        Raise(nameof(HasMultipleMarked));
        Raise(nameof(CanSetCredentials));
        Raise(nameof(ShowDeviceDetail));
        Raise(nameof(CountsText));
        RefreshPhases();
        Raise(nameof(ShowProgress));
        Raise(nameof(CanWake));   // wake works off the marked set, not the single selection
        // ⚠️ The single-device actions depend on the SIZE of the selection too, so they have to be
        // re-evaluated here and not only when the current row changes – marking a second device does not
        // change SelectedDevice, and without this the menu would stay enabled on a multi-row selection.
        Raise(nameof(CanSsh));
        Raise(nameof(CanSftp));
        Raise(nameof(CanRdp));
        Raise(nameof(CanTelnet));
        Raise(nameof(CanVnc));
        Raise(nameof(CanOpenFirmware));
    }

    public int MarkedCount => Marked.Count;
    public bool HasMultipleMarked => Marked.Count > 1;

    /// <summary>The counts in the status bar: total, then how many carry an IPv4 and an IPv6 address – a
    /// device can have one, the other or both, so the parts do not add up to the total and are not meant to.
    /// The filtered and marked counts join in only when they apply, so the common case stays quiet.</summary>
    public string CountsText
    {
        get
        {
            var all = _fleet.Snapshot();
            if (all.Count == 0) return "";

            var v4 = all.Count(d => d.Ip.Length > 0 && !d.Ip.Contains(':'));
            var v6 = all.Count(d => d.HasIpv6);

            var parts = new List<string>
            {
                T("Av_CountDevices", all.Count),
                T("Av_CountIpv4", v4),
                T("Av_CountIpv6", v6),
            };
            if (Devices.Count != all.Count) parts.Add(T("Av_CountShown", Devices.Count));
            if (_marked.Count > 1) parts.Add(T("Av_CountMarked", _marked.Count));
            return string.Join(" · ", parts);
        }
    }

    // The StatusLine / HasStatusMessage pair that used to live here is gone with the message strip it fed.
    // ⚠️ Removed rather than left in place: nothing bound them any more, and a public property that looks
    // like the way to show a message – but is wired to nothing – is how a message ends up written for a
    // display that does not exist. Action outcomes now go exactly two places: a failure to LastError (the
    // Connection errors tab, where it persists), and the few things worth interrupting for to a toast.

    // ⚠️ Session-only, and starts HIDDEN – the window opens with the whole height on the list for a clean
    // overview. Opening/closing is purely the user's choice now: the Appearance-menu toggle and the chevron.
    // There is NO auto-reveal any more – the pane springing open on a click was annoying. Its HEIGHT is still
    // persisted (DetailPaneHeight) – that is the layout choice worth remembering, not open/closed.
    private bool _showDetailPane;
    /// <summary>Whether the detail pane is shown. Off gives the whole window to the list.</summary>
    public bool ShowDetailPane
    {
        get => _showDetailPane;
        set
        {
            if (_showDetailPane == value) return;
            _showDetailPane = value;
            Raise(nameof(ShowDetailPane));
            Raise(nameof(EffectiveShowDetailPane));
        }
    }

    private bool _detailPaneAllowed = true;
    /// <summary>Whether the CURRENT tab has a detail pane at all: only the device lists (IPv4/IPv6) do – on
    /// the maps, backups and updates the pane showed stale device facts under unrelated content and just ate
    /// height. Set by the tab-change handler; not persisted (it follows the tab, not a user choice).</summary>
    public bool DetailPaneAllowed
    {
        get => _detailPaneAllowed;
        set
        {
            if (_detailPaneAllowed == value) return;
            _detailPaneAllowed = value;
            Raise(nameof(DetailPaneAllowed));
            Raise(nameof(EffectiveShowDetailPane));
        }
    }

    /// <summary>What the pane (and its resize thumb) actually bind to: the user's toggle AND the tab
    /// allowing one. The chevron button binds to <see cref="DetailPaneAllowed"/> only, so the user's choice
    /// survives visiting a tab without a pane.</summary>
    public bool EffectiveShowDetailPane => ShowDetailPane && DetailPaneAllowed;

    public double DetailPaneHeight
    {
        get => _appData.DetailPaneHeight is >= 70 and <= 2000 ? _appData.DetailPaneHeight : 220;
        set { _appData.DetailPaneHeight = value; SaveSettings(); }
    }

    /// <summary>True once the user has dragged the pane splitter, so the opening height is their exact size
    /// rather than the window-aware default.</summary>
    public bool DetailPaneHeightSet
    {
        get => _appData.DetailPaneHeightSet;
        set { _appData.DetailPaneHeightSet = value; SaveSettings(); }
    }

    /// <summary>⚠️ The detail pane describes exactly one device. With several marked it shows a note
    /// instead – displaying the facts of whichever one happens to be "selected" would attribute them to
    /// the whole selection.</summary>
    public bool ShowDeviceDetail => HasSelection && !HasMultipleMarked;

    /// <summary>Auto-refresh intervals offered in the toolbar, in seconds.</summary>
    public IReadOnlyList<int> IntervalChoices { get; } = new[] { 10, 30, 60, 120, 300, 600 };

    public int PollInterval
    {
        get => _appData.PollIntervalSeconds is >= 5 and <= 3600 ? _appData.PollIntervalSeconds : 30;
        set
        {
            if (value < 5) return;
            _appData.PollIntervalSeconds = value;
            SaveSettings();
            Raise(nameof(PollInterval));
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>Whether "set credentials" would do anything for the current selection – a login is only
    /// useful where TikMan has something to do with it. For a multi-device selection, true when at least
    /// one of them can use one.
    /// <para>The action is disabled rather than hidden: a missing button raises "where has it gone?", a
    /// greyed one with a reason answers the question on hover.</para></summary>
    public bool CanSetCredentials =>
        _marked.Count > 0 ? _marked.Any(d => d.CanUseCredentials) : _selected?.CanUseCredentials == true;
    public bool CanWake => Marked.Any(d => d.Mac.Length > 0);
    public bool CanBackup => _selected is { HasLogin: true };
    public bool CanVnc => SingleSelection && _selected is { VncPort: > 0 };

    // ---- "open X" gates -------------------------------------------------------------------------
    // Each of these answers exactly one question: would launching this actually reach something? An
    // enabled "Open RDP" on a printer is a promise the app cannot keep – the client opens, hangs on a
    // closed port and blames the user's network. Greyed out says "not here" immediately.
    //
    // ⚠️ Each gate mirrors the argument its handler already passes to the launcher (3389 for RDP, 23 for
    // Telnet, SshPort for SSH/SFTP), so the two cannot drift into "enabled but does nothing" or the more
    // annoying "greyed out but would have worked".

    /// <summary>⚠️ Every "open X" is off while more than one row is selected. These actions each open ONE
    /// window against ONE address, and the handlers work off <see cref="SelectedDevice"/> – which under a
    /// multi-row selection is whichever row the grid happens to call current. So the action would launch
    /// against an arbitrary member of the selection: not what "open a terminal" means with five rows
    /// highlighted, and not something the user could predict. Opening five terminals instead would be a
    /// different guess at the same ambiguous request. Refusing to guess is the honest answer.
    /// <para>Wake-on-LAN is deliberately NOT gated this way – it is a bulk action by design and sends to
    /// every marked MAC, which is unambiguous.</para></summary>
    private bool SingleSelection => Marked.Count <= 1;

    /// <summary>⚠️ A non-standard SSH port counts as available even when the scan never saw it open: the
    /// port scan only covers the well-known list, so a user who deliberately typed 2222 would otherwise be
    /// locked out of the very device they just configured. On the default port the scan result decides.</summary>
    public bool CanSsh => SingleSelection && _selected is { } d
                       && (d.OpenPorts.Contains(d.SshPort) || d.SshPort != 22);
    public bool CanSftp => CanSsh;
    public bool CanRdp => SingleSelection && _selected?.OpenPorts.Contains(3389) == true;
    public bool CanTelnet => SingleSelection && _selected?.OpenPorts.Contains(23) == true;

    /// <summary>Whether a firmware page exists for this device. Computed from the same call the action
    /// makes, so "enabled" and "opens something" are the same condition by construction – TikMan only knows
    /// where MikroTik and TP-Link/Omada publish, and guessing a URL for anyone else lands on a 404.</summary>
    public bool CanOpenFirmware => SingleSelection && FirmwareUrl().Length > 0;

    /// <summary>Whether the monitoring tab has anything to show. CPU is the reliable tell: it is only ever
    /// filled after a successful resource read, so an empty string means "never polled" (no login, not
    /// RouterOS, or unreachable) rather than an idle device.</summary>
    public bool HasMonitoring => _selected is { Cpu.Length: > 0 };

    /// <summary>Why the monitoring tab is empty – the two reasons need different answers from the user, so
    /// they get different sentences instead of one vague "no data".</summary>
    public string MonitoringHint => _selected is { HasLogin: false }
        ? T("Av_MonNeedsLogin")             // a login would fix it
        : T("Av_MonNotSupported");          // logged in, but this device does not answer resource queries

    /// <summary>Whether the details tab has any collected facts. Empty is normal for a device that only
    /// answered a ping, and saying so beats an empty panel that reads as a bug.</summary>
    public bool HasDetailFacts => _selected is { Info.Count: > 0 };

    /// <summary>Log reading is a RouterOS feature and needs a login; everything else has nothing to show.
    /// <c>CanUpdate</c> carries exactly that pair of conditions, so it is reused rather than re-derived.</summary>
    /// <summary>⚠️ The device's own log capability, not <c>CanUpdate</c>. Reusing that meant "MikroTik with
    /// a login", so a TP-Link switch – which reads its log fine but takes no firmware updates – would have
    /// had its log tab greyed out with the data sitting right there.</summary>
    public bool CanReadLogs => _selected is { CanReadLogs: true };

    public bool HasLastError => _lastError.Length > 0;

    private string _lastError = "";
    /// <summary>The last failure for the selected device – shown in its own tab so errors are findable
    /// after the status bar has moved on to something else.</summary>
    public string LastError { get => _lastError; private set { _lastError = value; Raise(nameof(LastError)); Raise(nameof(HasLastError)); } }

    private string _action = "";
    public string ActionResult
    {
        get => _action;
        private set
        {
            _action = value;
            Raise(nameof(ActionResult));
            // A failure is also kept on the Connection errors tab; the status line moves on with the next
            // action, so the tab is the durable record.
            if (value.Length > 0 && LooksLikeFailure(value)) LastError = $"{DateTime.Now:HH:mm:ss}  {value}";
            // No expiry timer: the strip has a fixed always-present row, so a lingering message costs no
            // layout, and the last line staying until the next one is easier to read than a strip that
            // keeps blanking itself.
        }
    }

    /// <summary>Heuristic: the launchers and backup paths return localised prose, so there is no error flag
    /// to test. Treat anything that isn't a plain success as worth keeping.</summary>
    private static bool LooksLikeFailure(string text) =>
        text.Contains('✗') || text.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || text.Contains("error", StringComparison.OrdinalIgnoreCase)
        || text.Contains("nicht", StringComparison.OrdinalIgnoreCase)
        || text.Contains("nöd", StringComparison.OrdinalIgnoreCase);

    public MainWindowViewModel()
    {
        _appData = DeviceStore.Load();
        _fleet = new FleetService(_appData);
        TopoEdit = new TopoEditing(_appData);
        _fleet.Changed += OnFleetChanged;

        // The picker shows adapter + its gateway – the CIDR is already in the range box next to it, so
        // repeating it there wastes the width that actually tells the networks apart.
        Adapters.Add(new ScanChoice(T("Av_AllNetworks"), ""));
        foreach (var s in FleetService.LocalSubnets())
            Adapters.Add(new ScanChoice(s.Gateway is { Length: > 0 } ? $"{s.Adapter} — GW {s.Gateway}" : s.Adapter, s.Cidr));
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Cidr.Length > 0) ?? Adapters[0];

        Refresh();
    }

    public void Scan()
    {
        // The target is redacted on the way into the log; it is recorded because "found nothing" is not
        // actionable without knowing what was swept.
        ActionLog.Write("scan.start", $"target={(string.IsNullOrWhiteSpace(ScanRange) ? "all local subnets" : ScanRange)}");
        _suppressRestart = false;
        _fleet.StartScan(string.IsNullOrWhiteSpace(ScanRange) ? null : ScanRange);
    }

    // A manual stop must win over "continuous scan" for one round, or the sweep the user just cancelled
    // restarts itself three seconds later. Cleared by the next manual Scan().
    private bool _suppressRestart;

    /// <summary>The scan button: starts when idle, stops when running – one button, two verbs.</summary>
    public void ToggleScan()
    {
        if (CanScan) { Scan(); return; }
        ActionLog.Write("scan.stop", "user");
        _suppressRestart = true;
        // One Stop ends everything on screen: the scan AND any credential re-read ("Accessing devices"),
        // which is a separate activity but reads to the user as the same "busy". Both are no-ops when idle.
        _fleet.StopScan();
        _fleet.StopRescan();
    }

    public string ScanButtonText => CanScan ? T("Av_BtnScan") : T("Av_StopScan");

    /// <summary>Re-runs the per-device checks for the marked devices, in the background. This is the
    /// context menu's "rescan" – and it runs automatically after credentials were saved for a selection.</summary>
    public void RescanMarked()
    {
        var ids = Marked.Select(d => d.Id).Where(id => id.Length > 0).ToList();
        if (ids.Count == 0) return;
        ActionLog.Write("rescan.selected", $"count={ids.Count}");
        ActionResult = T("Av_RescanStarted", ids.Count);
        _ = _fleet.RescanDevicesAsync(ids);
    }

    /// <summary>The logical topology (Internet → devices) – instant.</summary>
    /// <summary>The user's own topology contributions – node positions and hand-drawn nodes/links. Kept
    /// apart from the built map on purpose; see <see cref="TopoEditing"/>.</summary>
    public TopoEditing TopoEdit { get; }

    public TopoLayout BuildLogicalTopology() => _fleet.BuildLogicalTopology();

    /// <summary>The physical topology from forwarding tables – slow (reads every bridge), so awaited.</summary>
    public Task<TopoLayout> BuildPhysicalTopologyAsync() => _fleet.BuildPhysicalTopologyAsync();

    /// <summary>The username to pre-fill a login dialog with: the device's current one, or the vendor's factory
    /// default ("admin" for most, "ubnt" for Ubiquiti). The selected device's
    /// <see cref="DeviceSnapshot.User"/> is used when it already has one.</summary>
    public string LoginUserFor(DeviceSnapshot? d) =>
        // A username exists only when a real login was saved (Device.Username is empty otherwise, and a
        // password-less one is purged on load), so a set username IS the user's choice – keep it. Otherwise
        // suggest the vendor's factory default ("admin" for most, "ubnt" for Ubiquiti).
        d is { User.Length: > 0 } ? d.User : FleetService.DefaultUsername(d?.Vendor ?? "");

    /// <summary>Stores (or clears, when the password is empty) the login for the selected device via the
    /// fleet, which DPAPI/AES-encrypts it and persists – the password is never held in plaintext here.</summary>
    /// <summary>⚠️ Takes the device id explicitly. Reading <c>_selected</c> here meant the 30-second refresh
    /// could clear the selection while the dialog was open (a filter change is enough) – and the entered
    /// password was then dropped without a word. The caller captured the device before opening the dialog;
    /// that is the one it must be saved against.</summary>
    public void SetLogin(string deviceId, string user, string password)
    {
        if (deviceId.Length == 0) return;
        ActionLog.Write("login.set", password.Length > 0 ? "stored" : "cleared");
        if (!_fleet.SetLogin(deviceId, user, password)) { ActionLog.Write("login.failed", "device not found"); ActionResult = T("Av_LoginNotSaved"); return; }
        ActionResult = password.Length > 0 ? T("Av_LoggedIn") : T("Av_LoginRemoved");
        // The login exists to be used: re-probe this device with it right away, instead of leaving the
        // richer columns empty until the next background cycle happens to come round.
        if (password.Length > 0) _ = _fleet.RescanDevicesAsync(new[] { deviceId });
    }

    /// <summary>Exports the selected device's config (RouterOS .rsc / Zyxel .cfg) – the bytes are handed
    /// back for the caller to save; they can hold secrets, so they are never logged.</summary>
    public Task<FleetService.BackupData> BackupConfigAsync() =>
        _selected is null
            ? Task.FromResult(FleetService.BackupData.Fail(T("Av_NoDeviceSelected")))
            : _fleet.BackupConfigAsync(_selected.Id);

    /// <summary>Opens an interactive SSH shell to the selected device (needs a login). Returns the session
    /// or, on failure, SSH.NET's own reason – so the caller can show it and offer the external client.</summary>
    /// <param name="user">Typed credentials for a device with no stored login – used for this session
    /// only and never persisted. Null falls back to whatever is stored.</param>
    public Task<TikMan.Core.Api.SshTerminalSession.ConnectResult> OpenTerminalAsync(
        string? user = null, string? password = null) =>
        _selected is null
            ? Task.FromResult(new TikMan.Core.Api.SshTerminalSession.ConnectResult(null, "no device selected"))
            : _fleet.OpenTerminalDiagnosticAsync(_selected.Id, 120, 32, user, password);

    /// <summary>Surfaces a one-line result under the detail actions (used by the code-behind dialogs).</summary>
    public void ReportAction(string message) => ActionResult = message;

    /// <summary>Opens the vendor's firmware download page for the selected device.
    ///
    /// <para>⚠️ Keyed on the <b>identified</b> vendor, not on <c>Device.Vendor</c>. That enum is the
    /// connector kind, it defaults to MikroTik and nothing in the codebase ever assigns it – which is why
    /// the WPF client's version of this action, gated on <c>Vendor == DeviceVendor.TpLink</c>, could never
    /// fire on any device. Same rule the classifier uses, so it works here.</para>
    ///
    /// <para>Which page exists per vendor is <see cref="FirmwarePages"/>'s business: MikroTik has the
    /// RouterOS changelog, TP-Link a per-model download page, Zyxel the Download Library pre-filled with the
    /// model, and everyone else nothing that can be derived – which is said out loud rather than guessed
    /// at.</para></summary>
    /// <summary>The firmware page for the selection, or "" when none can be derived. Its own method so the
    /// menu gate (<see cref="CanOpenFirmware"/>) and the action ask the identical question.</summary>
    private string FirmwareUrl()
    {
        if (_selected is null) return "";
        // The hardware revision refines the TP-Link URL when a probe found one; without it the model page
        // still opens, one click further from the file. The firmware version, when known, upgrades the link
        // from the product page to the direct firmware download page.
        var revision = _selected.Info.FirstOrDefault(kv =>
            kv.Key.Contains("Revision", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        return FirmwarePages.UrlFor(_selected.Vendor, _selected.Model, revision, _selected.Firmware);
    }

    public void OpenFirmwarePage()
    {
        if (_selected is null) { ActionResult = T("Av_NoSelection"); return; }

        var vendor = _selected.Vendor;
        var url = FirmwareUrl();
        if (url.Length == 0)
        {
            ActionResult = T("Av_FirmwarePageUnknown", vendor.Length > 0 ? vendor : T("Av_UnknownVendor"));
            return;
        }
        Report(Launchers.OpenWeb(url));
    }

    /// <summary>Opens a web URL in the browser – used by the clickable Latest cell (the page a version was
    /// parsed from, or the download search for a "manual search" link).</summary>
    public void OpenWeb(string url)
    {
        if (url.Length > 0) Report(Launchers.OpenWeb(url));
    }

    /// <summary>Removes the selected device from the list (a stale entry). A later scan can find it again.</summary>
    public void RemoveSelected()
    {
        if (_selected is null) return;
        var name = _selected.Name;
        if (_fleet.RemoveDevice(_selected.Id)) { SelectedDevice = null; ActionResult = T("Av_Removed", name); }
    }

    // ---- status bar: public IP --------------------------------------------------------------------

    private string _publicV4 = "", _publicV6 = "";

    /// <summary>Looks up the public IPv4/IPv6 for the status bar. Best-effort: no connection simply means
    /// no text, never an error the user has to dismiss.</summary>
    public async Task LoadPublicIpAsync()
    {
        try
        {
            var ip = await PublicIpClient.GetAsync();
            _publicV4 = ip.V4 ?? "";
            _publicV6 = ip.V6 ?? "";
        }
        catch { _publicV4 = _publicV6 = ""; }
    }

    /// <summary>The two halves of the public-address readout – separate, because each is its own click
    /// target: clicking the IPv4 copies only the IPv4, clicking the IPv6 only the IPv6. The clipboard
    /// lives on the TopLevel, so the window does the actual copying.</summary>
    public string PublicV4 => _publicV4;
    public string PublicV6 => _publicV6;
    public string PublicV4Display => _publicV4.Length > 0 || _publicV6.Length > 0 ? $"🌐 IPv4 {(_publicV4.Length > 0 ? _publicV4 : "–")}" : "";
    public string PublicV6Display => _publicV4.Length > 0 || _publicV6.Length > 0 ? $"IPv6 {(_publicV6.Length > 0 ? _publicV6 : "–")}" : "";

    /// <summary>Reports the outcome of a clipboard copy in the status line.</summary>
    public void ReportCopied(string what) => ActionResult = T("Av_Copied", what);

    // ---- feedback buttons -------------------------------------------------------------------------

    /// <summary>Opens a path (an SMB share) in the platform's file manager.</summary>
    public void OpenPath(string path) => OpenUrl(path);

    /// <summary>Opens a folder (an SMB share, typically) in the desktop's file manager.
    ///
    /// <para>⚠️ Not <see cref="OpenUrl"/>. ShellExecute on a bare UNC path is unreliable – it depends on
    /// a handler being registered for the path form and fails silently when there isn't one, which is
    /// exactly how the share buttons ended up doing nothing at all. Naming the file manager explicitly
    /// (explorer / open / xdg-open) is what actually opens the window. Failures are reported instead of
    /// swallowed, so a share that cannot be reached says so.</para></summary>
    public void OpenFolder(string path)
    {
        if (path.Length == 0) return;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                // ⚠️ Quoted, and no exit-code check: explorer.exe returns 1 even when it opened fine.
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Process.Start("open", path);
            else Process.Start("xdg-open", path);
        }
        catch (Exception ex) { ActionResult = T("Av_OpenFolderFailed", path, ex.Message); }
    }

    // ---- external clients -------------------------------------------------------------------------
    // Each launcher returns "" on success or a message for the status bar – never a silent no-op.

    /// <summary>Opens a remote desktop session to the device (3389, or whichever RDP port is open).</summary>
    public void LaunchRdp(DeviceSnapshot d) =>
        Report(Launchers.Rdp(d.Ip, d.OpenPorts.Contains(3389) ? 3389 : 0));

    /// <summary>Opens an SSH session in an external client (the built-in terminal handles stored logins).
    /// ⚠️ Pass an explicit username – the stored one if any, otherwise the vendor default (ubnt for Ubiquiti,
    /// admin elsewhere) via <see cref="LoginUserFor"/>. Handing OpenSSH a bare host would make it silently log
    /// in as the local OS account, which is never right for a network device; with a username it prompts only
    /// for the password ("ask in the SSH window") instead of leaking the operator's own login name.</summary>
    public void LaunchSsh(DeviceSnapshot d) =>
        Report(Launchers.Ssh(d.Ip, d.SshPort, LoginUserFor(d),
            _appData.UseExternalSshClient, _appData.ExternalSshClientPath ?? ""));

    /// <summary>Opens an SFTP session – WinSCP when configured, else the desktop's sftp:// handler.
    /// The stored password is only decrypted at all when the user turned that option on; otherwise it
    /// never leaves the credential store and the external client prompts for it.</summary>
    public void LaunchSftp(DeviceSnapshot d)
    {
        var password = _appData.PassPasswordToExternalClients ? _fleet.PasswordFor(d.Id) : "";
        Report(Launchers.Sftp(d.Ip, d.SshPort, d.User.Trim(), _appData.WinScpPath ?? "", password));
    }

    public void LaunchTelnet(DeviceSnapshot d) =>
        Report(Launchers.Telnet(d.Ip, d.OpenPorts.Contains(23) ? 23 : 0));

    public void LaunchFtp(string url) => Report(Launchers.Ftp(url));
    public void LaunchRtsp(string url) => Report(Launchers.Rtsp(url, _appData.VlcPath ?? ""));

    private void Report(string message) { if (message.Length > 0) ActionResult = message; }

    private const string IssueBase = "https://github.com/pgadient/TikMan/issues/new";

    /// <summary>The issue tracker's bug form, with the fields TikMan already knows filled in.
    ///
    /// <para>⚠️ Version and OS are prefilled deliberately: they are required in the form, they are the two
    /// answers a reporter is least able to give reliably from memory, and every wrong version turns into a
    /// round of questions before anything can be reproduced. The log stays the reporter's own decision –
    /// it goes through the review window first.</para>
    ///
    /// <para>Note the log dialog opens before this – see OnReportProblem.</para></summary>
    public void ReportProblem()
    {
        var version = AppVersion.Text(typeof(MainWindowViewModel).Assembly);
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
               : "Linux";
        OpenUrl($"{IssueBase}?template=bug_report.yml"
                + $"&version={Uri.EscapeDataString(version)}"
                + $"&os={Uri.EscapeDataString(os)}");
    }

    /// <summary>Writes the action log out for attaching to a report; returns the path, or "" on failure.</summary>
    public string SaveActionLog() => ActionLog.SaveToFile();

    /// <summary>Opens the folder holding the settings and the log, so the file can be found without
    /// knowing where the app keeps things.</summary>
    public void OpenLogFolder() => OpenPath(DeviceStore.StorageDirectory);
    public void RequestFeature() => OpenUrl($"{IssueBase}?template=feature_request.yml");
    public void BuyCoffee() => OpenUrl("https://ko-fi.com/pascalmontico");

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { /* no browser handler */ }
    }

    // ---- built-in web server ----------------------------------------------------------------------

    private WebServerController? _web;
    private WebServerController Web => _web ??= new WebServerController(_fleet, _appData);

    public bool WebRunning => _web?.IsRunning == true;
    public string WebToggleText => WebRunning ? T("Av_WebStop") : T("Av_WebStart");
    public string WebUrl => _web?.Url ?? "";

    /// <summary>Starts or stops the built-in dashboard; the result goes to the action line.</summary>
    /// <summary>True when the web server can't start yet because it has no login. The server itself also
    /// refuses (and returns Av_WebNeedCreds), but that message only lands in ActionResult now – so the
    /// window checks this first and shows a real dialog instead.</summary>
    public bool WebCredentialsMissing =>
        (_appData.WebServerUser?.Trim() ?? "").Length == 0 ||
        CredentialProtector.Unprotect(_appData.WebServerEncryptedPassword).Length == 0;

    public void ToggleWebServer()
    {
        // ⚠️ The SUCCESS message is not pushed to the action line. "web server running" is a state, not an
        // event: as a one-off message it sat there long after the server had been stopped again, which is
        // precisely wrong. The banner below the window shows it while it is true and disappears when it
        // stops – state belongs somewhere that can stop being true.
        // A failure still has to be said, though: a port already in use is an event, and swallowing it
        // would leave a click that visibly does nothing at all.
        var wanted = !WebRunning;                      // what the click is asking for
        var message = wanted ? Web.Start() : Web.Stop();
        if (WebRunning != wanted) ActionResult = message;

        Raise(nameof(WebRunning));
        Raise(nameof(WebToggleText));
        Raise(nameof(WebUrl));
    }

    public void OpenWebServer()
    {
        if (WebRunning) Web.OpenInBrowser();
        else ActionResult = T("Av_WebNotRunning");
    }

    /// <summary>Releases what the window owns: stops the dashboard so its port is free again.</summary>
    public void ShutDown()
    {
        try { if (WebRunning) Web.Stop(); } catch { /* shutting down anyway */ }
    }

    /// <summary>Startup chores: auto-start the dashboard if configured, and tell the user when a newer
    /// TikMan exists.
    /// <para>⚠️ This build deliberately does <b>not</b> replace itself. The self-update in the Windows/WPF
    /// client swaps a <c>TikMan-&lt;version&gt;-win-&lt;arch&gt;.exe</c> release asset; this cross-platform
    /// build has no such per-variant asset, so it reports the new version and leaves installing to the
    /// user rather than pretending to update.</para></summary>
    public async Task StartupAsync()
    {
        if (_appData.WebServerAutoStart) ToggleWebServer();

        // The update check goes first: no point making the user sit through a scan that a restart throws away.
        if (_appData.CheckForUpdates)
        {
            var current = AppVersion.Current(typeof(MainWindowViewModel).Assembly);
            var (newer, latest, name) = await AppUpdater.CheckVersionAsync(current);
            if (newer && latest is not null) ActionResult = T("Av_AppUpdate", AppVersion.Text(latest), name);
        }

        // "No startup scan" means exactly that: touch nothing, show the restored list as it was saved.
        // Restored devices stay grey (never checked this session) until a scan or a refresh says otherwise.
        if (!_appData.NoInitialScan) Scan();
        else if (Devices.Count > 0) Status = T("Status_RestoredUnchecked");
    }

    /// <summary>Sends a Wake-on-LAN magic packet to every marked device that has a MAC. Devices without one
    /// are skipped silently – there is nothing to address the packet to.</summary>
    public void Wake()
    {
        var macs = Marked.Select(d => d.Mac).Where(m => m.Length > 0).Distinct().ToList();
        if (macs.Count == 0) { ActionResult = T("Av_WolNoMac"); return; }

        var sent = macs.Count(WakeOnLan.Send);
        ActionResult = macs.Count == 1
            ? (sent == 1 ? T("Av_WolSent", macs[0]) : T("Av_WolFailed"))
            : T("Av_WolSentMany", sent, macs.Count);
    }

    /// <summary>Sends the magic packet to one MAC typed by the user – for a machine that is already powered
    /// down and therefore not in the list at all.</summary>
    public void WakeMac(string mac)
    {
        var clean = mac.Trim();
        if (clean.Length == 0) return;
        ActionResult = WakeOnLan.Send(clean) ? T("Av_WolSent", clean) : T("Av_WolFailed");
    }

    /// <summary>Wakes whatever the user typed: a MAC directly, or an IP that a previous scan has already
    /// tied to one.
    ///
    /// <para>⚠️ An IP can only be resolved from what TikMan already knows. Wake-on-LAN is a layer-2 frame
    /// addressed to a MAC, and a machine that is switched off answers no ARP – so an address never seen
    /// before genuinely cannot be woken, and saying so is better than sending a packet nowhere.</para></summary>
    public void WakeTarget(string target)
    {
        var clean = target.Trim();
        if (clean.Length == 0) return;

        // A MAC has hex groups separated by : or -; anything with a dot is an address.
        if (clean.Contains('.') || (clean.Contains(':') && clean.Count(c => c == ':') > 5))
        {
            var known = _fleet.Snapshot().FirstOrDefault(d =>
                d.Ip.Equals(clean, StringComparison.OrdinalIgnoreCase) && d.Mac.Length > 0);
            if (known is null) { ActionResult = T("Av_WolUnknownAddress", clean); return; }
            ActionResult = WakeOnLan.Send(known.Mac)
                ? T("Av_WolSentFor", known.Mac, clean)
                : T("Av_WolFailed");
            return;
        }

        WakeMac(clean);
    }

    /// <summary>Stores the same login on every marked device. Returns how many were saved, so the caller can
    /// report it – a bulk action that silently half-worked is worse than one that says so.</summary>
    public int SetLoginForMarked(IReadOnlyList<string> deviceIds, string user, string password)
    {
        var saved = deviceIds.Where(id => id.Length > 0 && _fleet.SetLogin(id, user, password)).ToList();
        ActionResult = saved.Count == deviceIds.Count
            ? (password.Length > 0 ? T("Av_LoggedInMany", saved.Count) : T("Av_LoginRemovedMany", saved.Count))
            : T("Av_LoginPartial", saved.Count, deviceIds.Count);
        // Same as the single-device path: a stored login is put to work immediately, for exactly the
        // devices it was saved on.
        if (password.Length > 0 && saved.Count > 0) _ = _fleet.RescanDevicesAsync(saved);
        return saved.Count;
    }

    /// <summary>Turns phase-state transitions into ticker lines: Pending→Running says "Starting: …",
    /// Running→Done says "Finished: …". Skips (and anything noticed only after the scan ended) stay
    /// silent. The seen-states dictionary is what makes each transition fire exactly once even though this
    /// runs on every refresh.</summary>
    private readonly Dictionary<string, FleetService.PhaseState> _phaseTicker = new();

    private void TickPhases(bool scanning)
    {
        foreach (var ph in _fleet.Phases)
        {
            _phaseTicker.TryGetValue(ph.Name, out var prev);
            if (prev == ph.State) continue;
            _phaseTicker[ph.Name] = ph.State;
            if (!scanning) continue;
            var label = T("Av_Phase" + char.ToUpperInvariant(ph.Name[0]) + ph.Name[1..]);
            if (ph.State == FleetService.PhaseState.Running) ActionResult = T("Av_PhaseStarting", label);
            else if (ph.State == FleetService.PhaseState.Done && prev == FleetService.PhaseState.Running)
                ActionResult = T("Av_PhaseFinished", label);
        }
    }

    /// <summary>Orders a device by its IPv4 address as a number. Anything without a v4 address (a v6-only
    /// neighbour) sorts to the end rather than to the top, where an empty cell would look like a glitch.</summary>
    private static uint SortKey(DeviceSnapshot d)
    {
        var parts = d.Ip.Split('.');
        if (parts.Length != 4) return uint.MaxValue;
        uint value = 0;
        foreach (var p in parts)
        {
            if (!byte.TryParse(p, out var b)) return uint.MaxValue;
            value = (value << 8) | b;
        }
        return value;
    }

    // FleetService raises Changed on background threads – hop onto the UI thread before touching the
    // observable collection.
    private void OnFleetChanged() => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Raised around the list rebuild, so the view can save and restore what the collection swap
    /// destroys – keyboard focus, mainly. During a scan the rebuild runs every quarter second, and each one
    /// throws away every realized row; whichever row held focus takes the focus down with it.</summary>
    public event Action? ListRefreshing;
    public event Action? ListRefreshed;

    /// <summary>Everything a device row actually displays, as one string.
    ///
    /// <para>⚠️ Not <c>DeviceSnapshot.Equals</c>. It is a record, so its generated equality compares the
    /// <b>collection references</b> in Info / Ipv6 / Badges – and those are freshly built on every refresh,
    /// so two snapshots of a device that has not changed at all never compare equal. Using that would have
    /// replaced every row every time and reconciling would have bought nothing.</para></summary>
    private static string RowSignature(DeviceSnapshot d) =>
        string.Join("",
            d.Name, d.Ip, d.Ipv6Summary, d.Mac, d.MacVendor, d.Vendor, d.Model, d.KindText,
            d.Serial, d.Os, d.Firmware, d.LatestVersion, d.LatestVersionUrl, d.InstalledRelease, d.UpdateRelease,
            d.Cpu, d.Memory, d.Uptime, d.Status,
            // ⚠️ LoginError is part of the signature. Without it, a login going from OK to rejected (or back)
            // left the row's signature unchanged, so Sync never replaced the row and the broken-key indicator
            // never appeared – for every vendor, MikroTik included. Detection worked; the row just never
            // redrew. HasLogin alone doesn't cover it: a wrong password still "has a login".
            d.HasLogin, d.LoginError, d.IsGateway, d.NothingAnswered, d.SharesStatus,
            string.Join(",", d.Badges.Select(b => b.Name)),
            string.Join(",", d.Shares),
            // The row's expandable area is built from these, so a change there has to redraw the row too.
            d.Info.Count, d.Ipv6.Count);

    /// <summary>Brings an observable collection to match <paramref name="desired"/> with the fewest possible
    /// changes: items are matched by <paramref name="key"/>, and an item is only replaced when its
    /// <paramref name="signature"/> differs. Order follows <paramref name="desired"/>.
    ///
    /// <para>The point is what does <b>not</b> happen – an untouched row is never removed and re-added, so
    /// its container, selection and expanded state survive a refresh untouched.</para></summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired,
        Func<T, string> key, Func<T, string> signature)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var wantKey = key(desired[i]);

            if (i < target.Count && key(target[i]) == wantKey)
            {
                if (signature(target[i]) != signature(desired[i])) target[i] = desired[i];
                continue;
            }

            // Is it further down (items before it were removed)? Drop what is in the way rather than
            // rebuilding the tail, so rows below an removed one keep their containers.
            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (key(target[j]) == wantKey) { found = j; break; }

            if (found >= 0)
            {
                for (var k = found - 1; k >= i; k--) target.RemoveAt(k);
                if (signature(target[i]) != signature(desired[i])) target[i] = desired[i];
            }
            else target.Insert(i, desired[i]);
        }

        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    private void Refresh()
    {
        ListRefreshing?.Invoke();
        var keepId = _selected?.Id;              // survive the rebuild (the 30 s monitor fires Changed too)
        // Same for the marked set: the snapshots are new objects, so re-resolve the marks by id or a
        // multi-selection would quietly collapse to nothing within 30 seconds.
        var markedIds = _marked.Select(d => d.Id).ToHashSet();
        // Snapshots are rebuilt on every refresh, so carry the open/closed state of each row across by id –
        // otherwise a row you expanded snaps shut again within 30 seconds.
        var expanded = Devices.Where(d => d.IsExpanded).Select(d => d.Id).ToHashSet();
        var known = Devices.Select(d => d.Id).ToHashSet();

        // ⚠️ Sorted by IPv4 as the default order, and numerically – as text, .10 sorts before .9 and the
        // list looks shuffled. Clicking a column header still overrides this: the grid keeps its own sort
        // over the source, so this only decides what an untouched list looks like.
        var snapshot = _fleet.Snapshot().Where(Matches).OrderBy(SortKey).ToList();
        // Rows you already saw keep whatever you set; rows appearing for the first time follow the setting.
        foreach (var s in snapshot)
            s.IsExpanded = known.Contains(s.Id) ? expanded.Contains(s.Id) : _appData.ExpandRowsByDefault;
        // ⚠️ Reconciled in place, NOT Clear() + re-add. A scan raises Changed about four times a second, and
        // clearing destroys every row container – so an expanded row was torn down and rebuilt on each
        // refresh, and the code-behind re-opened it a dispatcher tick later. That collapse/re-open cycle is
        // exactly the flicker: the row was closing and reopening four times a second. Now a row is only
        // touched when what it displays has actually changed; everything else keeps its container, its
        // expanded state and its scroll position.
        // ⚠️ The IPv4 tab lists devices that HAVE an IPv4 address. A neighbour found only over IPv6 gets a
        // row keyed on its v6 address, and that row was landing here too – with the v6 address printed in
        // the "IPv4" column, which is simply a false statement about the device. It belongs in the IPv6 tab,
        // which is where it now appears alone. The status bar counts both, so nothing goes missing.
        Sync(Devices, snapshot.Where(HasIpv4Address).ToList(), d => d.Id, RowSignature);

        var v6Devices = snapshot.Where(d => d.HasIpv6).ToList();
        Sync(Ipv6Devices, v6Devices, d => d.Id, RowSignature);

        var v6Rows = new List<Ipv6Row>();
        var group = 0;
        foreach (var s in v6Devices)
        {
            group++;
            var first = true;
            // Sorted per device so a device's own addresses appear in a stable order rather than in
            // whatever sequence the neighbour cache happened to report them.
            foreach (var e in s.Ipv6Entries.OrderBy(e => e.Address, StringComparer.OrdinalIgnoreCase))
            {
                v6Rows.Add(new Ipv6Row(s, e, group, first));
                first = false;
            }
        }
        Sync(Ipv6Rows, v6Rows, r => r.Device.Id + "|" + r.Address,
            r => RowSignature(r.Device) + "|" + r.Tag.Text + "|" + r.Probed + "|" +
                 string.Join(",", r.Badges.Select(b => b.Name)));
        Raise(nameof(HasIpv6Devices));
        if (keepId is not null) SelectedDevice = Devices.FirstOrDefault(d => d.Id == keepId);
        if (markedIds.Count > 0) _marked = Devices.Where(d => markedIds.Contains(d.Id)).ToList();
        Raise(nameof(CountsText));
        RefreshPhases();
        // (ShowProgress is raised by the CanScan setter below – raising it here, before CanScan changes,
        // is exactly the bug that left a grey bar standing after every scan.)

        var (scanning, _, _, count) = _fleet.Status;

        // ONE continuous bar, 0 → 100 % across the whole scan, with fixed weights: the address sweep is
        // 65 %, the four discovery protocols 10 % (each contributes its quarter the moment its listening
        // window closes; a skipped one counts as closed), and the meta collection the last 25 %, counted
        // device by device. So discovery fills the bar to 75 % and the metas take it from there. Every
        // term only ever grows, so the bar never runs backwards.
        double composite = 0;
        var discoveryDone = false;
        if (scanning)
        {
            double sweep = 0, meta = 0;
            int protoDone = 0, protoTotal = 0;
            foreach (var ph in _fleet.Phases)
                switch (ph.Name)
                {
                    case "scan":
                        sweep = ph.State == FleetService.PhaseState.Done ? 1 : Math.Max(0, ph.Progress);
                        break;
                    case "probe":
                        discoveryDone = ph.State is FleetService.PhaseState.Running or FleetService.PhaseState.Done;
                        meta = ph.State == FleetService.PhaseState.Done ? 1 : Math.Max(0, ph.Progress);
                        break;
                    default:
                        protoTotal++;
                        if (ph.State is FleetService.PhaseState.Done or FleetService.PhaseState.Skipped)
                            protoDone++;
                        break;
                }
            _protocolsDone = protoTotal > 0 && protoDone == protoTotal;
            composite = 0.65 * sweep
                      + (protoTotal > 0 ? 0.10 * protoDone / protoTotal : 0.10)
                      + 0.25 * meta;
        }
        var pct = composite > 0 ? $"{(int)(composite * 100)} % — " : "";
        ScanIndeterminate = scanning && composite <= 0;
        _discoveryDone = discoveryDone;
        ScanProgress = composite;

        RescanRunning = _fleet.Rescanning;
        RescanProgress = _fleet.RescanProgress;

        // The ticker: phase transitions become short-lived one-liners in the message strip ("Starting: …",
        // "Finished: …"). What the scan IS doing, not how far it is – the bar and the counts cover that.
        TickPhases(scanning);
        Status = scanning
            ? T("Av_StatusScanning", pct, count)
            : count == 0 ? T("Av_StatusNoDevices")
                         : T("Av_StatusCount", count);
        // ⚠️ The button is "Stop" while EITHER a scan OR a credential re-read ("Accessing devices") is
        // running, so that re-read can be stopped too – it is not part of the scan (a saved login triggers
        // it) but the user sees one "busy" state and expects one Stop. ShowProgress stays scan-only (the
        // re-read has its own bar). RescanRunning is read from the fleet just above.
        CanScan = !scanning && !RescanRunning;
        ShowProgress = scanning;
        Raise(nameof(ShowNoLoginBanner));

        // Continuous scan: start the next sweep after a breather. ⚠️ The delay is not cosmetic – a scan that
        // fails immediately (bad CIDR, no matching interface) completes inside the notification that
        // triggered it, so restarting synchronously here span at full tilt and flooded the dispatcher.
        if (!scanning && _continuousScan && !_restartQueued && !_suppressRestart)
        {
            _restartQueued = true;
            DispatcherTimer.RunOnce(() =>
            {
                _restartQueued = false;
                if (_continuousScan && CanScan) Scan();
            }, TimeSpan.FromSeconds(3));
        }

        ListRefreshed?.Invoke();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One entry in the adapter/subnet picker: what to show, and the CIDR to scan ("" = all).</summary>
public sealed record ScanChoice(string Display, string Cidr)
{
    public override string ToString() => Display; // the ComboBox shows this
}
