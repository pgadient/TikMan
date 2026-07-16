using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>How one device's turn ended. <see cref="Partial"/> is a real third case, not a rounding of
/// the other two: config saved but the binary failed (or the reverse) is neither "backed up" nor
/// "failed", and folding it into either one misreports the run.</summary>
public enum BackupOutcome { Saved, Partial, Failed }

/// <summary>Row in the backup assistant.</summary>
public class BackupItemViewModel : INotifyPropertyChanged
{
    public DeviceViewModel Device { get; }

    /// <summary>Is this really a RouterOS box? Asked of <see cref="DeviceViewModel.IdentifiedVendor"/>,
    /// which means the board code (REST or MNDP) or the MikroTik OUI – the same test the classifier
    /// trusts everywhere else.
    /// <para>⚠️ NOT <c>Model.Vendor == DeviceVendor.MikroTik</c>, which is what this used to ask.
    /// That enum picks the *connector* (REST vs the TP-Link SSH path), it defaults to MikroTik and
    /// nothing in the code ever assigns it – so it is true for every device that exists, and the old
    /// "binary backup on MikroTik only" guard excluded exactly nothing.</para></summary>
    public bool IsMikroTik => Device.IdentifiedVendor == "MikroTik";

    /// <summary>Whether this device can hand over a config export (.rsc). RouterOS only today, and not
    /// because of a policy: <see cref="DeviceViewModel.DownloadConfigAsync"/> goes through the RouterOS
    /// REST client or <c>/export</c> over SSH, and nothing else speaks either. Until now the assistant
    /// tried it on every device with a login and let the others fail one by one.</summary>
    public bool CanConfig => IsMikroTik;

    /// <summary>Whether a full binary backup (.backup) can be pulled. Also RouterOS only – it is
    /// <c>/system backup save</c> plus SCP.</summary>
    public bool CanBinary => IsMikroTik;

    /// <summary>False when TikMan can't back this device up at all – the row says so and sits out.</summary>
    public bool IsSupported => CanConfig || CanBinary;

    public BackupItemViewModel(DeviceViewModel device)
    {
        Device = device;
        _isSelected = true;
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(nameof(IsSelected)); } }

    private bool _canSelect = true;
    public bool CanSelect { get => _canSelect; set { _canSelect = value; Notify(nameof(CanSelect)); } }

    // No StateText: the free-text column is gone, and what it carried during a run is in the log, which
    // scrolls, keeps its history and can be read afterwards. What it also carried was "no backup
    // available" – a standing fact about a row, not run output, so it has nothing to say to a log. That
    // now reads off the greyed-out tick and the vendor column instead.

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Backup assistant: pick which devices (with a stored login) to back up, in which order, and
/// whether to also pull the full binary backup (.backup) from MikroTik devices. All transports are the
/// secure ones – HTTPS/SSH, never HTTP.
/// <para>Lives directly in the Backup tab rather than behind a button: the tab was a title, a sentence
/// and a button that opened this – a door into a room you were already standing in.</para></summary>
public partial class BackupAllView : UserControl
{
    private readonly ObservableCollection<BackupItemViewModel> _items = new();
    private BackupMethod _method = BackupMethod.Auto;
    private CancellationTokenSource? _cts;
    private bool _running;
    private string _folder = "";

    public BackupAllView()
    {
        InitializeComponent();
        ItemGrid.ItemsSource = _items;
    }

    /// <summary>True while a backup run is in progress – the tab must not swap the list underneath it.</summary>
    public bool IsRunning => _running;

    /// <summary>(Re)fills the list, called each time the tab is shown: devices come and go with every
    /// scan, and a stale list would offer to back up something that is no longer there. Ignored while a
    /// run is going, and the selection/order the user set is kept for devices that are still around.</summary>
    public void Load(IReadOnlyList<DeviceViewModel> candidates, BackupMethod method)
    {
        _method = method;
        if (_running) return;

        var previous = _items.ToDictionary(i => i.Device);
        foreach (var old in _items) old.PropertyChanged -= Item_PropertyChanged;
        _items.Clear();
        foreach (var device in candidates)
        {
            var item = previous.TryGetValue(device, out var kept) ? kept : new BackupItemViewModel(device);
            // A device TikMan can't back up sits the run out rather than being tried and failing once
            // it's under way: unticked, and its tick greyed so it can't be put back.
            if (!item.IsSupported) { item.IsSelected = false; item.CanSelect = false; }
            item.PropertyChanged += Item_PropertyChanged;
            _items.Add(item);
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStartEnabled();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupItemViewModel.IsSelected)) UpdateStartEnabled();
    }

    private void BackupType_Changed(object sender, RoutedEventArgs e) => UpdateStartEnabled();

    /// <summary>Start is only offered when it would actually do something: at least one kind of backup
    /// asked for, and at least one ticked device that can produce that kind. A disabled button says
    /// "nothing to do here" before the click; an error in the log says it afterwards.</summary>
    private void UpdateStartEnabled()
    {
        if (StartButton is null) return; // fires from the checkbox during InitializeComponent
        bool wantConfig = ConfigCheck.IsChecked == true;
        bool wantBinary = BinaryCheck.IsChecked == true;
        StartButton.IsEnabled = !_running && _items.Any(i =>
            i.IsSelected && ((wantConfig && i.CanConfig) || (wantBinary && i.CanBinary)));
    }

    /// <summary>Asks where the backups go. Called on every Start – this writes files, so confirming the
    /// destination each time beats silently reusing the folder from an hour ago. The last one seeds the
    /// dialog, so saying yes twice is two clicks, not two navigations. False when the user cancels.</summary>
    private bool ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = T("Dlg_FolderTitle") };
        if (_folder.Length > 0) dialog.InitialDirectory = _folder;
        var owner = Window.GetWindow(this);
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (ok != true) return false;
        _folder = dialog.FolderName;
        FolderText.Text = _folder;
        return true;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (_running) return;
        if (ItemGrid.SelectedItem is not BackupItemViewModel item) return;
        int index = _items.IndexOf(item);
        int target = index + delta;
        if (target < 0 || target >= _items.Count) return;
        _items.Move(index, target);
        ItemGrid.SelectedItem = item;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); Log(T("Ua_StopRequested")); }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        bool wantConfig = ConfigCheck.IsChecked == true;
        bool wantBinary = BinaryCheck.IsChecked == true;
        // Only the rows that a run would actually touch. The button is disabled when this is empty, so
        // this is a guard, not a message.
        var selected = _items.Where(i => i.IsSelected && ((wantConfig && i.CanConfig) || (wantBinary && i.CanBinary)))
                             .ToList();
        if (selected.Count == 0) return;
        if (!ChooseFolder()) return; // cancelled the folder dialog – they know why nothing happened

        _running = true;
        _cts = new CancellationTokenSource();
        SetUiRunning(true);
        BackupProgress.Minimum = 0; BackupProgress.Maximum = selected.Count; BackupProgress.Value = 0;
        BackupProgress.Visibility = Visibility.Visible;

        int done = 0, partial = 0, failed = 0;
        try
        {
            foreach (var item in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                switch (await BackupDeviceAsync(item, wantConfig, wantBinary, _cts.Token))
                {
                    case BackupOutcome.Saved: done++; break;
                    case BackupOutcome.Partial: partial++; break;
                    default: failed++; break;
                }
                BackupProgress.Value += 1;
            }
            Log(T("Ba_Done", done, partial, failed, _folder));
        }
        catch (OperationCanceledException) { Log(T("Ua_Stopped")); }
        finally
        {
            _running = false; _cts.Dispose(); _cts = null;
            SetUiRunning(false);
            BackupProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Backs one device up with whatever was asked for *and* it can do. Either half may be off,
    /// so success is "everything attempted worked" rather than "the config worked": a run for the binary
    /// alone must not be reported as done because a config we never fetched didn't fail.</summary>
    private async Task<BackupOutcome> BackupDeviceAsync(BackupItemViewModel item, bool wantConfig, bool wantBinary,
        CancellationToken ct)
    {
        var device = item.Device;
        var stamp = DateTime.Now;
        Log(T("Ba_DeviceHeader", device.Name, device.Host));

        bool doConfig = wantConfig && item.CanConfig;
        bool doBinary = wantBinary && item.CanBinary;
        string? rscName = null, binName = null;
        bool configFailed = false;

        if (doConfig)
        {
            Log(T("Ba_StateConfig"));
            var result = await device.DownloadConfigAsync(ct);
            if (result is not { } data)
            {
                Log(T("Ba_StateFailed", device.LastError));
                if (!doBinary) return BackupOutcome.Failed;
                configFailed = true;   // still try the binary – it travels a different path
            }
            else
            {
                try
                {
                    rscName = BackupNaming.SuggestFileName(data.Identity, device.Board, device.Host, stamp);
                    File.WriteAllText(Path.Combine(_folder, rscName), data.Config);
                }
                catch (Exception ex)
                {
                    Log(T("Ba_StateFailed", ex.Message));
                    if (!doBinary) return BackupOutcome.Failed;
                    rscName = null;
                    configFailed = true;
                }
            }
        }

        if (doBinary)
        {
            Log(T("Ba_StateBinary"));
            // Name it after the config when we have one, so the pair sits together in the folder.
            var name = rscName is { } r
                ? (r.EndsWith(".rsc") ? r[..^4] : r) + ".backup"
                : BackupNaming.SuggestFileName(device.Name, device.Board, device.Host, stamp)
                    .Replace(".rsc", "") + ".backup";
            var ok = await device.DownloadFullBackupAsync(_method, device.Model.SshPort,
                Path.Combine(_folder, name), ct);
            if (ok) binName = name;
            else
            {
                Log(rscName is null ? T("Ba_StateFailed", device.LastError)
                                    : T("Ba_StateConfigOnly", device.LastError));
                // The config landed but the binary didn't: partial, not done and not failed.
                return rscName is null ? BackupOutcome.Failed : BackupOutcome.Partial;
            }
        }

        var files = string.Join(" + ", new[] { rscName, binName }.Where(n => n is not null));
        var done = configFailed ? T("Ba_StateBinaryOnly", device.LastError) : T("Ba_StateDone");
        Log(done + (files.Length > 0 ? " – " + files : ""));
        return configFailed ? BackupOutcome.Partial : BackupOutcome.Saved;
    }

    private void SetUiRunning(bool running)
    {
        StopButton.IsEnabled = running;
        UpButton.IsEnabled = !running;
        DownButton.IsEnabled = !running;
        ConfigCheck.IsEnabled = !running;
        BinaryCheck.IsEnabled = !running;
        // A device we can't back up stays unpickable when the run ends, not just while it runs.
        foreach (var item in _items) item.CanSelect = !running && item.IsSupported;
        UpdateStartEnabled();
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        LogBox.ScrollToEnd();
    }
}
