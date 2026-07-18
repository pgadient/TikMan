using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using TikMan.Core.Discovery;
using TikMan.Core.Fleet;
using TikMan.Core.Storage;

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

    public ObservableCollection<DeviceSnapshot> Devices { get; } = new();

    private string _status = "Bereit — «Scannen» startet die Suche im lokalen Netz.";
    public string Status { get => _status; private set { _status = value; Raise(nameof(Status)); } }

    private bool _canScan = true;
    public bool CanScan { get => _canScan; private set { _canScan = value; Raise(nameof(CanScan)); } }

    /// <summary>Adapter / subnet picker entries: "Alle lokalen Netze" plus one per up interface (its real
    /// CIDR). Choosing one fills <see cref="ScanRange"/>, which the scan actually uses (also editable).</summary>
    public ObservableCollection<ScanChoice> Adapters { get; } = new();

    private ScanChoice? _selectedAdapter;
    public ScanChoice? SelectedAdapter
    {
        get => _selectedAdapter;
        set { _selectedAdapter = value; Raise(nameof(SelectedAdapter)); if (value is not null) ScanRange = value.Cidr; }
    }

    private string _scanRange = "";
    public string ScanRange { get => _scanRange; set { _scanRange = value; Raise(nameof(ScanRange)); } }

    private DeviceSnapshot? _selected;
    public DeviceSnapshot? SelectedDevice
    {
        get => _selected;
        set
        {
            _selected = value;
            Raise(nameof(SelectedDevice));
            Raise(nameof(HasSelection));
            Raise(nameof(CanWake));
        }
    }

    public bool HasSelection => _selected is not null;
    public bool CanWake => _selected is { Mac.Length: > 0 };

    private string _action = "";
    public string ActionResult { get => _action; private set { _action = value; Raise(nameof(ActionResult)); } }

    public MainWindowViewModel()
    {
        _appData = DeviceStore.Load();
        _fleet = new FleetService(_appData);
        _fleet.Changed += OnFleetChanged;

        Adapters.Add(new ScanChoice("Alle lokalen Netze", ""));
        foreach (var s in FleetService.LocalSubnets()) Adapters.Add(new ScanChoice($"{s.Adapter} — {s.Cidr}", s.Cidr));
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Cidr.Length > 0) ?? Adapters[0];

        Refresh();
    }

    public void Scan() => _fleet.StartScan(string.IsNullOrWhiteSpace(ScanRange) ? null : ScanRange);

    /// <summary>The logical topology (Internet → devices) – instant.</summary>
    public TopoLayout BuildLogicalTopology() => _fleet.BuildLogicalTopology();

    /// <summary>The physical topology from forwarding tables – slow (reads every bridge), so awaited.</summary>
    public Task<TopoLayout> BuildPhysicalTopologyAsync() => _fleet.BuildPhysicalTopologyAsync();

    /// <summary>The username to pre-fill a login dialog with: the device's current one, or the app's
    /// default. The selected device's <see cref="DeviceSnapshot.User"/> is used when it already has one.</summary>
    public string LoginUserFor(DeviceSnapshot? d) =>
        d is { User.Length: > 0 } ? d.User : (_appData.DefaultUsername ?? "");

    /// <summary>Stores (or clears, when the password is empty) the login for the selected device via the
    /// fleet, which DPAPI/AES-encrypts it and persists – the password is never held in plaintext here.</summary>
    public void SetLogin(string user, string password)
    {
        if (_selected is null) return;
        _fleet.SetLogin(_selected.Id, user, password);
        ActionResult = password.Length > 0 ? "Angemeldet." : "Anmeldung entfernt.";
    }

    /// <summary>Sends a Wake-on-LAN magic packet to the selected device.</summary>
    public void Wake()
    {
        var mac = _selected?.Mac ?? "";
        if (mac.Length == 0) return;
        var ok = WakeOnLan.Send(mac);
        ActionResult = ok ? $"Magic Packet an {mac} gesendet." : "Senden fehlgeschlagen.";
    }

    // FleetService raises Changed on background threads – hop onto the UI thread before touching the
    // observable collection.
    private void OnFleetChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var keepId = _selected?.Id;              // survive the rebuild (the 30 s monitor fires Changed too)
        var snapshot = _fleet.Snapshot();
        Devices.Clear();
        foreach (var s in snapshot) Devices.Add(s);
        if (keepId is not null) SelectedDevice = Devices.FirstOrDefault(d => d.Id == keepId);

        var (scanning, progress, _, count) = _fleet.Status;
        var pct = progress >= 0 ? $"{(int)(progress * 100)} % — " : "";
        Status = scanning
            ? $"Scan läuft… {pct}{count} Geräte bisher"
            : count == 0 ? "Keine Geräte — «Scannen» startet die Suche im lokalen Netz."
                         : $"{count} Geräte";
        CanScan = !scanning;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One entry in the adapter/subnet picker: what to show, and the CIDR to scan ("" = all).</summary>
public sealed record ScanChoice(string Display, string Cidr)
{
    public override string ToString() => Display; // the ComboBox shows this
}
