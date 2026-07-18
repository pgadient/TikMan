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

    public ObservableCollection<DeviceSnapshot> Devices { get; } = new();

    private string _status = "Bereit — «Scannen» startet die Suche im lokalen Netz.";
    public string Status { get => _status; private set { _status = value; Raise(nameof(Status)); } }

    private bool _canScan = true;
    public bool CanScan { get => _canScan; private set { _canScan = value; Raise(nameof(CanScan)); } }

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
        _fleet = new FleetService(DeviceStore.Load());
        _fleet.Changed += OnFleetChanged;
        Refresh();
    }

    public void Scan() => _fleet.StartScan();

    /// <summary>The logical topology (Internet → devices) – instant.</summary>
    public TopoLayout BuildLogicalTopology() => _fleet.BuildLogicalTopology();

    /// <summary>The physical topology from forwarding tables – slow (reads every bridge), so awaited.</summary>
    public Task<TopoLayout> BuildPhysicalTopologyAsync() => _fleet.BuildPhysicalTopologyAsync();

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
