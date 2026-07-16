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

/// <summary>Row in the backup assistant.</summary>
public class BackupItemViewModel : INotifyPropertyChanged
{
    public DeviceViewModel Device { get; }
    public bool IsMikroTik => Device.Model.Vendor == DeviceVendor.MikroTik;

    public BackupItemViewModel(DeviceViewModel device)
    {
        Device = device;
        _isSelected = true;
        _stateText = "";
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(nameof(IsSelected)); } }

    private bool _canSelect = true;
    public bool CanSelect { get => _canSelect; set { _canSelect = value; Notify(nameof(CanSelect)); } }

    private string _stateText;
    public string StateText { get => _stateText; set { _stateText = value; Notify(nameof(StateText)); } }

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
        _items.Clear();
        foreach (var device in candidates)
            _items.Add(previous.TryGetValue(device, out var old) ? old : new BackupItemViewModel(device));

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) { Log(T("Ua_NoneSelected")); return; }
        if (!ChooseFolder()) return; // cancelled the folder dialog – they know why nothing happened

        bool alsoBinary = AlsoBinaryCheck.IsChecked == true;
        _running = true;
        _cts = new CancellationTokenSource();
        SetUiRunning(true);
        BackupProgress.Minimum = 0; BackupProgress.Maximum = selected.Count; BackupProgress.Value = 0;
        BackupProgress.Visibility = Visibility.Visible;

        int done = 0, failed = 0;
        try
        {
            foreach (var item in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                if (await BackupDeviceAsync(item, alsoBinary, _cts.Token)) done++; else failed++;
                BackupProgress.Value += 1;
            }
            Log(T("Ba_Done", done, failed, _folder));
        }
        catch (OperationCanceledException) { Log(T("Ua_Stopped")); }
        finally
        {
            _running = false; _cts.Dispose(); _cts = null;
            SetUiRunning(false);
            BackupProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<bool> BackupDeviceAsync(BackupItemViewModel item, bool alsoBinary, CancellationToken ct)
    {
        var device = item.Device;
        var stamp = DateTime.Now;
        Log(T("Ba_DeviceHeader", device.Name, device.Host));

        item.StateText = T("Ba_StateConfig");
        var result = await device.DownloadConfigAsync(ct);
        if (result is not { } data)
        {
            item.StateText = T("Ba_StateFailed", device.LastError);
            Log(item.StateText);
            return false;
        }
        string rscName;
        try
        {
            rscName = BackupNaming.SuggestFileName(data.Identity, device.Board, device.Host, stamp);
            File.WriteAllText(Path.Combine(_folder, rscName), data.Config);
        }
        catch (Exception ex) { item.StateText = T("Ba_StateFailed", ex.Message); Log(item.StateText); return false; }

        // Optional: the full binary backup (MikroTik only, over SSH). Other vendors ignore the flag.
        // Only the failure was ever logged, so a binary backup that worked left no trace at all: the
        // run said "done – <name>.rsc" and the .backup sitting next to it was never mentioned.
        string? binName = null;
        if (alsoBinary && item.IsMikroTik)
        {
            item.StateText = T("Ba_StateBinary");
            Log(item.StateText);
            var name = rscName.EndsWith(".rsc") ? rscName[..^4] + ".backup" : rscName + ".backup";
            var ok = await device.DownloadFullBackupAsync(_method, device.Model.SshPort, Path.Combine(_folder, name), ct);
            if (!ok)
            {
                item.StateText = T("Ba_StateConfigOnly", device.LastError);
                Log(item.StateText);
                return true; // the config was saved – count it as a (partial) success
            }
            binName = name;
        }
        item.StateText = T("Ba_StateDone");
        Log(T("Ba_StateDone") + " – " + rscName + (binName is null ? "" : " + " + binName));
        return true;
    }

    private void SetUiRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        UpButton.IsEnabled = !running;
        DownButton.IsEnabled = !running;
        AlsoBinaryCheck.IsEnabled = !running;
        foreach (var item in _items) item.CanSelect = !running;
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        LogBox.ScrollToEnd();
    }
}
