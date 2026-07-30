using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using TikMan.Core.Fleet;

namespace TikMan.App.Avalonia;

/// <summary>Multi-device backup assistant, hosted as a tab (like the WPF client): pick which capable devices
/// to back up (config always, the binary .backup optionally for MikroTik), choose a target folder once, then
/// each device's file is written there. Config-OK + binary-fail is a partial success – the config is safe.
/// The backup bytes can hold secrets, so they are only written to disk – never logged.</summary>
public partial class BackupAllView : UserControl
{
    private FleetService? _fleet;
    private readonly ObservableCollection<BackupRow> _rows = new();
    // ⚠️ FindControl, not x:Name fields – the Avalonia generator doesn't reliably wire the fields here.
    private DataGrid _grid = null!;
    private ProgressBar _progress = null!;
    private TextBox _log = null!;
    private CheckBox _alsoBinary = null!;
    private CheckBox _configExport = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private TextBlock _emptyNotice = null!;

    /// <summary>Cancels the running batch; cancellation is checked between devices so a file being written
    /// is always finished.</summary>
    private CancellationTokenSource? _cancel;

    public BackupAllView()
    {
        AvaloniaXamlLoader.Load(this);
        _grid = this.FindControl<DataGrid>("Grid")!;
        _progress = this.FindControl<ProgressBar>("Progress")!;
        _log = this.FindControl<TextBox>("Log")!;
        _alsoBinary = this.FindControl<CheckBox>("AlsoBinary")!;
        _configExport = this.FindControl<CheckBox>("ConfigExport")!;
        _startButton = this.FindControl<Button>("StartButton")!;
        _stopButton = this.FindControl<Button>("StopButton")!;
        _emptyNotice = this.FindControl<TextBlock>("EmptyNotice")!;
        _grid.ItemsSource = _rows;
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    { _cancel?.Cancel(); Append("Stopping after the current device…"); }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => Move(+1);

    /// <summary>Moves the selected row one place, keeping it selected so repeated clicks walk it along.</summary>
    private void Move(int delta)
    {
        if (_grid.SelectedItem is not BackupRow row) return;
        var from = _rows.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _rows.Count) return;
        _rows.Move(from, to);
        _grid.SelectedItem = row;
    }

    /// <summary>Binds the view to the live fleet and fills the grid. Called once by the main window.</summary>
    public void Attach(FleetService fleet)
    {
        _fleet = fleet;
        Reload();
    }

    /// <summary>Rebuilds the row list from the current inventory (devices appear as scans find them).</summary>
    public void Reload()
    {
        if (_fleet is null) return;
        var keep = _rows.Where(r => !r.Selected).Select(r => r.Id).ToHashSet();
        _rows.Clear();
        foreach (var d in _fleet.Snapshot().Where(d => d.CanConfigBackup && d.HasLogin))
            _rows.Add(new BackupRow(d.Id, d.Name, d.Ip, d.KindText, d.CanConfigBackup, d.CanFullBackup)
                { Selected = !keep.Contains(d.Id) });
        // ⚠️ Shown and hidden, not appended. This used to go into the log, which never clears – so the
        // sentence written before the first scan was still sitting under a full list afterwards, flatly
        // contradicting the rows above it. Tied to the list being empty, it cannot outlive the fact.
        _emptyNotice.IsVisible = _rows.Count == 0;
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Reload();
    private void OnSelectAll(object? sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = true; }
    private void OnSelectNone(object? sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = false; }

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_fleet is null) return;
        var chosen = _rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0) { Append("Nothing selected."); return; }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) { Append("No file dialog available."); return; }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Target folder for the backups",
            AllowMultiple = false,
        });
        var dir = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(dir)) { Append("Cancelled."); return; }

        bool alsoBinary = _alsoBinary.IsChecked == true;
        bool wantConfig = _configExport.IsChecked == true;
        if (!wantConfig && !alsoBinary) { Append("Nothing to fetch – pick config export, the binary backup, or both."); return; }

        _cancel = new CancellationTokenSource();
        _startButton.IsEnabled = false;
        _stopButton.IsVisible = true;
        _progress.IsVisible = true;
        _progress.Value = 0;
        Append($"Backing up {chosen.Count} device(s) to {dir}…");

        // ⚠️ try/finally: this is an async void handler, so anything escaping the loop would both leave the
        // Start button dead forever and take the process down.
        int done = 0, saved = 0, partial = 0, failed = 0;
        try
        {
            foreach (var row in chosen)
            {
                if (_cancel.IsCancellationRequested) { Append("Stopped."); break; }

                row.Status = "running…";
                try
                {
                    bool configOk = true, binaryOk = true, wroteSomething = false;

                    if (wantConfig)
                    {
                        var config = await _fleet.BackupConfigAsync(row.Id);
                        if (config.Ok)
                        {
                            await WriteAsync(dir, config);
                            Append($"✓ {row.Name}: {config.FileName}");
                            wroteSomething = true;
                        }
                        else { configOk = false; Append($"✗ {row.Name}: {config.Message}"); }
                    }

                    if (alsoBinary && row.CanBinary)
                    {
                        var full = await _fleet.FullBackupAsync(row.Id);
                        if (full.Ok)
                        {
                            await WriteAsync(dir, full);
                            Append($"✓ {row.Name}: {full.FileName}");
                            wroteSomething = true;
                        }
                        else { binaryOk = false; Append($"⚠ {row.Name}: binary backup failed ({full.Message})"); }
                    }

                    // Config saved but the binary failed is a partial success – the config is what matters.
                    if (!wroteSomething) { row.Status = "Failed"; failed++; }
                    else if (!configOk || !binaryOk) { row.Status = "Partial"; partial++; }
                    else { row.Status = "Saved"; saved++; }
                }
                catch (Exception ex) { row.Status = "Failed: " + ex.Message; failed++; Append($"✗ {row.Name}: {ex.Message}"); }

                done++;
                _progress.Value = (double)done / chosen.Count;
            }

            Append($"Done — {saved} saved, {partial} partial, {failed} failed.");
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            _startButton.IsEnabled = true;
            _stopButton.IsVisible = false;
            _progress.IsVisible = false;
        }
    }

    private static async Task WriteAsync(string dir, FleetService.BackupData data) =>
        await File.WriteAllBytesAsync(Path.Combine(dir, data.FileName), data.Bytes);

    private void Append(string line)
    {
        _log.Text = _log.Text is { Length: > 0 } ? _log.Text + "\n" + line : line;
        _log.CaretIndex = _log.Text.Length;
    }
}

/// <summary>One selectable row in the backup grid – Selected and Status change during the run.</summary>
public sealed class BackupRow : INotifyPropertyChanged
{
    public BackupRow(string id, string name, string ip, string kindText, bool canConfig, bool canBinary)
    {
        Id = id; Name = name; Ip = ip; KindText = kindText; CanConfig = canConfig; CanBinary = canBinary;
    }

    public string Id { get; }
    public string Name { get; }
    public string Ip { get; }
    public string KindText { get; }
    public bool CanConfig { get; }
    public bool CanBinary { get; }

    // One column per backup kind, so the grid itself answers "what can this device do?" – which is what
    // the intro paragraph above the list used to have to explain in prose.
    public string ConfigText => CanConfig ? "✓" : "✗";
    public string BinaryText => CanBinary ? "✓" : "✗";

    private bool _selected;
    public bool Selected { get => _selected; set { _selected = value; Raise(nameof(Selected)); } }

    private string _status = "";
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
