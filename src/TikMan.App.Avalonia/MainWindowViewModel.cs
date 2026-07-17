using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using TikMan.Core.Fleet;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

/// <summary>The device-list view model: a thin binding layer over the shared <see cref="FleetService"/>.
/// The service does all the work (scan/enrich/classify/reachability) UI-free in Core; this just mirrors
/// its snapshots into an observable collection and marshals its change events onto the UI thread.</summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly FleetService _fleet;

    public ObservableCollection<DeviceSnapshot> Devices { get; } = new();

    private string _status = "Bereit — «Scannen» startet die Suche im lokalen Netz.";
    public string Status { get => _status; private set { _status = value; Raise(nameof(Status)); } }

    private bool _canScan = true;
    public bool CanScan { get => _canScan; private set { _canScan = value; Raise(nameof(CanScan)); } }

    public MainWindowViewModel()
    {
        _fleet = new FleetService(DeviceStore.Load());
        _fleet.Changed += OnFleetChanged;
        Refresh();
    }

    public void Scan() => _fleet.StartScan();

    // FleetService raises Changed on background threads – hop onto the UI thread before touching the
    // observable collection.
    private void OnFleetChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var snapshot = _fleet.Snapshot();
        Devices.Clear();
        foreach (var s in snapshot) Devices.Add(s);

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
