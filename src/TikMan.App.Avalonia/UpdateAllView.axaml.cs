using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TikMan.Core.Fleet;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

/// <summary>Multi-device update assistant, hosted as a tab (like the WPF client). "Check for updates"
/// queries each RouterOS device (HTTPS-REST → SSH), fills installed/available and pre-selects the ones with
/// a pending update; "install selected" triggers the install (SSH → REST), which downloads and reboots the
/// device. Checking alone can never leave a device half-updated.
/// <para>Devices are processed in grid order, which the ▲▼ buttons change. That matters: the rule of thumb
/// is edge devices first and the uplink router last – except under CAPsMAN, where the controller goes first
/// because it carries its APs along.</para></summary>
public partial class UpdateAllView : UserControl
{
    private FleetService? _fleet;
    private readonly ObservableCollection<UpdateRow> _rows = new();
    // ⚠️ FindControl, not x:Name fields (see BackupAllView).
    private DataGrid _grid = null!;
    private ProgressBar _progress = null!;
    private TextBox _log = null!;
    private Button _checkButton = null!;
    private Button _installButton = null!;
    private Button _stopButton = null!;
    private CheckBox _continueOnError = null!;
    private CheckBox _waitForDevice = null!;
    private ComboBox _channelForAll = null!;
    private CheckBox _oneChannelForAll = null!;
    private TextBlock _emptyNotice = null!;
    private AppData? _appData;
    /// <summary>Suppresses the persist/apply handlers while the controls are being filled from the stored
    /// settings – otherwise restoring the saved state counts as the user changing it.</summary>
    private bool _loading;

    /// <summary>Cancels the running batch. Cancellation lands between devices, never mid-install: aborting
    /// a device that is already writing its firmware is how you brick one.</summary>
    private CancellationTokenSource? _cancel;

    public UpdateAllView()
    {
        AvaloniaXamlLoader.Load(this);
        _grid = this.FindControl<DataGrid>("Grid")!;
        _progress = this.FindControl<ProgressBar>("Progress")!;
        _log = this.FindControl<TextBox>("Log")!;
        _checkButton = this.FindControl<Button>("CheckButton")!;
        _installButton = this.FindControl<Button>("InstallButton")!;
        _stopButton = this.FindControl<Button>("StopButton")!;
        _continueOnError = this.FindControl<CheckBox>("ContinueOnError")!;
        _waitForDevice = this.FindControl<CheckBox>("WaitForDevice")!;
        _channelForAll = this.FindControl<ComboBox>("ChannelForAll")!;
        _oneChannelForAll = this.FindControl<CheckBox>("OneChannelForAll")!;
        _emptyNotice = this.FindControl<TextBlock>("EmptyNotice")!;
        _grid.ItemsSource = _rows;
        // Drag a row to reorder the run order (and click-to-select, so the ▲▼ buttons have a target).
        RowReorder.Enable(_grid, _rows);

        // ⚠️ Without "(unchanged)": this combo is the fleet-wide channel, and "leave every device as it is"
        // is what the checkbox being OFF already means. Offering it here would be a second, contradictory
        // way to say the same thing.
        _channelForAll.ItemsSource = UpdateRow.Channels.Where(c => c != UpdateRow.KeepChannel).ToList();
    }

    public void Attach(FleetService fleet, AppData appData)
    {
        _fleet = fleet;
        _appData = appData;

        // Restore the stored choice. Both handlers are muted while this runs, so re-showing the saved state
        // does not itself write it back – or, worse, apply it to every row on startup.
        _loading = true;
        _oneChannelForAll.IsChecked = appData.OneUpdateChannelForAll;
        var stored = appData.DefaultUpdateChannel;
        _channelForAll.SelectedItem =
            (_channelForAll.ItemsSource as IReadOnlyList<string>)?.FirstOrDefault(c => c == stored)
            ?? "stable";
        _loading = false;

        Reload();
    }

    /// <summary>Rebuilds the row list from the current inventory, keeping any versions already checked.</summary>
    public void Reload()
    {
        if (_fleet is null) return;
        var known = _rows.ToDictionary(r => r.Id);
        _rows.Clear();
        foreach (var d in _fleet.Snapshot().Where(d => d.CanUpdate && d.HasLogin))
        {
            var row = new UpdateRow(d.Id, d.Name, d.Ip, d.KindText)
            {
                Channel = ChannelLabel(_fleet.UpdateChannelOf(d.Id)),
            };
            if (known.TryGetValue(d.Id, out var old))
            {
                row.Installed = old.Installed; row.Latest = old.Latest;
                row.Status = old.Status; row.Selected = old.Selected;
                row.Channel = old.Channel;
            }
            _rows.Add(row);
        }
        // Every rebuild re-applies the channel mode, so a row created while "one channel for all" is on
        // arrives locked to the fleet value instead of showing an editable "(unchanged)".
        ApplyChannelMode();
        // ⚠️ Shown and hidden, not appended. This used to go into the log, which never clears – so the
        // sentence written before the first scan sat under a full list afterwards, flatly contradicting the
        // rows above it. Tied to the list being empty, it cannot outlive the fact. (Same fix as the backup
        // tab.)
        _emptyNotice.IsVisible = _rows.Count == 0;
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Reload();
    private void OnSelectAll(object? sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = true; }
    private void OnSelectNone(object? sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = false; }
    private void OnStop(object? sender, RoutedEventArgs e) { _cancel?.Cancel(); Append("Stopping after the current device…"); }

    /// <summary>Applies one channel to every row – the common case is "put the whole fleet on long-term".
    /// Only has an effect while the checkbox is on; otherwise each row keeps its own.</summary>
    private void OnChannelForAll(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _channelForAll.SelectedItem is not string channel) return;
        if (_appData is not null) { _appData.DefaultUpdateChannel = channel; SaveSettings(); }
        if (_oneChannelForAll.IsChecked == true)
            foreach (var r in _rows) r.Channel = channel;
    }

    /// <summary>Switches between "one channel for the whole fleet" and "each device its own".</summary>
    private void OnOneChannelToggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = _oneChannelForAll.IsChecked == true;
        if (_appData is not null) { _appData.OneUpdateChannelForAll = on; SaveSettings(); }
        ApplyChannelMode();
    }

    /// <summary>Puts the rows into the mode the checkbox says: on ⇒ every row shows (and is locked to) the
    /// fleet channel; off ⇒ each row is editable again and falls back to what the device is actually set
    /// to, which is "(unchanged)" for a device that has never been given one.</summary>
    private void ApplyChannelMode()
    {
        var on = _oneChannelForAll.IsChecked == true;
        var fleetChannel = _channelForAll.SelectedItem as string ?? "stable";
        // Snapshot: setting r.Channel moves a bound ComboBox, whose SelectionChanged used to bubble back
        // into a Reload() that clears _rows. The tab handler now blocks that re-entrancy, but iterating a
        // copy keeps this loop safe regardless of what a property change triggers downstream.
        foreach (var r in _rows.ToList())
        {
            r.ChannelEditable = !on;
            if (on) r.Channel = fleetChannel;
            else if (_fleet is not null) r.Channel = ChannelLabel(_fleet.UpdateChannelOf(r.Id));
        }
    }

    /// <summary>⚠️ Not swallowed: a settings write that fails must not look like one that worked – the
    /// choice would then be quietly back to the old value at the next start.</summary>
    private void SaveSettings()
    {
        if (_appData is null) return;
        try { DeviceStore.Save(_appData); }
        catch (Exception ex) { Append($"Could not save the channel setting: {ex.Message}"); }
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => RowReorder.MoveSelected(_grid, _rows, -1);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => RowReorder.MoveSelected(_grid, _rows, +1);

    /// <summary>Runs a check automatically when the tab is opened, so the Installed/Available columns are
    /// current without the user having to press the button each time. A no-op while a check or install is
    /// already running (the run in progress owns the grid), and when there is nothing to check.</summary>
    public void AutoCheckOnOpen()
    {
        if (_fleet is null || _rows.Count == 0 || _cancel is not null) return;
        OnCheck(this, new RoutedEventArgs());
    }

    private async void OnCheck(object? sender, RoutedEventArgs e)
    {
        if (_fleet is null || _rows.Count == 0) return;
        BeginRun();
        Append("Checking for updates…");

        // ⚠️ In parallel (gated), not one after another. A check is a network round trip per device, and on a
        // slow appliance the SSH/HTTPS handshake alone is seconds – measured, checking a dozen devices one at
        // a time took most of a minute of pure waiting. They are independent reads, so a bounded fan-out cuts
        // that to roughly the slowest single device. (The INSTALL stays strictly serial – OnInstall – because
        // order matters there: edge first, uplink last, and each device reboots.)
        // Snapshot _rows first: the bound collection stays live (Refresh clears it, ▲▼ reorder it, a tab
        // switch runs) while the checks are in flight. The row objects are shared, so status updates still
        // land in the grid; the continuations resume on the UI thread (no ConfigureAwait), so touching
        // row/log/progress is safe and the shared counters are only ever incremented there.
        var rows = _rows.ToList();
        int parallel = _appData is { ParallelDeviceReads: >= 1 and <= 32 } a ? a.ParallelDeviceReads : 8;
        int done = 0, available = 0;
        using var gate = new SemaphoreSlim(parallel);
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                if (_cancel!.IsCancellationRequested) return;
                await gate.WaitAsync();
                try
                {
                    if (_cancel!.IsCancellationRequested) return;
                    row.Status = "checking…";
                    // Remember the choice before the check so it survives a restart even if the device is down.
                    _fleet.SetUpdateChannel(row.Id, ChannelValue(row.Channel));

                    var info = await _fleet.CheckUpdateAsync(row.Id, ChannelValue(row.Channel));
                    if (info is null) { row.Status = "Check failed"; Append($"✗ {row.Name}: no response"); }
                    else
                    {
                        row.Installed = info.InstalledVersion;
                        row.Latest = info.LatestVersion;
                        if (info.UpdateAvailable) { row.Status = "Update available"; row.Selected = true; available++; Append($"● {row.Name}: {info.InstalledVersion} → {info.LatestVersion}"); }
                        else { row.Status = "Up to date"; row.Selected = false; Append($"○ {row.Name}: {info.InstalledVersion} (up to date)"); }
                    }
                }
                catch { row.Status = "Check failed"; Append($"✗ {row.Name}: error"); }
                finally
                {
                    gate.Release();
                    done++;
                    _progress.Value = rows.Count > 0 ? (double)done / rows.Count : 1;
                }
            }));

            Append(_cancel!.IsCancellationRequested
                ? $"Stopped — {available} update(s) available so far."
                : $"Done — {available} update(s) available.");
        }
        finally { EndRun(installEnabled: available > 0); }
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (_fleet is null) return;
        var chosen = _rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0) { Append("Nothing selected."); return; }

        BeginRun();
        bool keepGoing = _continueOnError.IsChecked == true;
        bool waitBack = _waitForDevice.IsChecked == true;
        Append($"Installing {chosen.Count} update(s) — the devices will reboot…");

        int done = 0, ok = 0, fail = 0;
        try
        {
            foreach (var row in chosen)
            {
                if (_cancel!.IsCancellationRequested) { Append("Stopped."); break; }

                row.Status = "installing…";
                var triggered = await _fleet.InstallUpdateAsync(row.Id);
                if (triggered)
                {
                    row.Status = "Installed (rebooting)"; ok++;
                    Append($"✓ {row.Name}: triggered, device is rebooting");
                    if (waitBack) await WaitForDeviceAsync(row, _cancel.Token);
                }
                else
                {
                    row.Status = "Failed"; fail++;
                    Append($"✗ {row.Name}: not triggered");
                    // ⚠️ Stopping on the first failure is the safer default for a chain: if an edge device
                    // did not take its update, carrying on to the uplink can cut the path to the rest.
                    if (!keepGoing) { Append("Stopped after the error (“continue on error” is off)."); break; }
                }
                done++;
                _progress.Value = (double)done / chosen.Count;
            }

            Append($"Done — {ok} triggered, {fail} failed. Give the devices a moment, then run «Check for updates» again.");
        }
        finally
        {
            // The installed devices are rebooting, so their versions are stale – a fresh check has to run
            // before installing again.
            EndRun(installEnabled: false);
        }
    }

    /// <summary>Waits (bounded) for a rebooting device to answer again, so the next device in the chain is
    /// not touched while its uplink is still down. Gives up after the timeout rather than hanging the run.</summary>
    private async Task WaitForDeviceAsync(UpdateRow row, CancellationToken ct)
    {
        row.Status = "waiting for reboot…";
        var deadline = DateTime.UtcNow.AddMinutes(5);
        // Let it actually go down first – a device answers for a second or two after the command lands.
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }

        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return;
            if (await _fleet!.IsReachableAsync(row.Id))
            {
                row.Status = "Installed (back online)";
                Append($"  {row.Name}: back online");
                return;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
        }
        row.Status = "Installed (still offline)";
        Append($"  ⚠ {row.Name}: still not answering after 5 minutes — continuing anyway");
    }

    /// <summary>Holds the background refresh off while this assistant is working; released in EndRun.</summary>
    private IDisposable? _refreshHold;

    private void BeginRun()
    {
        _cancel = new CancellationTokenSource();
        // ⚠️ An install reboots the device. A refresh talking to it mid-reboot yields failures that describe
        // the refresh's timing rather than the device – and it competes for the same SSH/REST session.
        _refreshHold = _fleet?.SuspendRefresh();
        _checkButton.IsEnabled = false;
        _installButton.IsEnabled = false;
        _stopButton.IsVisible = true;
        _progress.IsVisible = true;
        _progress.Value = 0;
    }

    private void EndRun(bool installEnabled)
    {
        _refreshHold?.Dispose();
        _refreshHold = null;
        _cancel?.Dispose();
        _cancel = null;
        _checkButton.IsEnabled = true;
        _installButton.IsEnabled = installEnabled;
        _stopButton.IsVisible = false;
        _progress.IsVisible = false;
    }

    /// <summary>UI label → the value RouterOS expects. The first entry means "leave the device alone".</summary>
    private static string ChannelValue(string label) =>
        label == UpdateRow.KeepChannel ? "" : label;

    private static string ChannelLabel(string value) =>
        value.Length == 0 ? UpdateRow.KeepChannel : value;

    private void Append(string line)
    {
        _log.Text = _log.Text is { Length: > 0 } ? _log.Text + "\n" + line : line;
        _log.CaretIndex = _log.Text.Length;
    }
}

/// <summary>One row in the update grid – Selected/Installed/Latest/Status change during check/install.</summary>
public sealed class UpdateRow : INotifyPropertyChanged
{
    /// <summary>Pseudo-channel meaning "don't touch what the device is set to".</summary>
    public const string KeepChannel = "(unchanged)";

    /// <summary>The RouterOS release channels, plus the leave-alone entry. Shared by the per-row picker and
    /// the "for all" combo above the grid.</summary>
    public static readonly IReadOnlyList<string> Channels =
        new[] { KeepChannel, "stable", "long-term", "testing", "development" };

    /// <summary>Instance view of <see cref="Channels"/> so the per-row ComboBox can bind to it.</summary>
    public IReadOnlyList<string> ChannelChoices => Channels;

    public UpdateRow(string id, string name, string ip, string kindText)
    {
        Id = id; Name = name; Ip = ip; KindText = kindText;
    }

    public string Id { get; }
    public string Name { get; }
    public string Ip { get; }
    public string KindText { get; }

    private bool _selected;
    public bool Selected { get => _selected; set { _selected = value; Raise(nameof(Selected)); } }

    private string _installed = "";
    public string Installed { get => _installed; set { _installed = value; Raise(nameof(Installed)); } }

    private string _latest = "";
    public string Latest { get => _latest; set { _latest = value; Raise(nameof(Latest)); } }

    private string _status = "";
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }

    private string _channel = KeepChannel;
    public string Channel { get => _channel; set { _channel = value; Raise(nameof(Channel)); } }

    /// <summary>False while "one channel for all" is on – the row's picker then shows the fleet value and
    /// is not editable, so there is only ever one place the channel comes from.</summary>
    private bool _channelEditable = true;
    public bool ChannelEditable { get => _channelEditable; set { _channelEditable = value; Raise(nameof(ChannelEditable)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
