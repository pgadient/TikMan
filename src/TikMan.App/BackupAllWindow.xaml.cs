using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
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
/// secure ones – HTTPS/SSH, never HTTP.</summary>
public partial class BackupAllWindow : Window
{
    private readonly ObservableCollection<BackupItemViewModel> _items = new();
    private readonly BackupMethod _method;
    private CancellationTokenSource? _cts;
    private bool _running;
    private string _folder = "";

    public BackupAllWindow(List<DeviceViewModel> candidates, BackupMethod method)
    {
        InitializeComponent();
        _method = method;
        foreach (var device in candidates) _items.Add(new BackupItemViewModel(device));
        ItemGrid.ItemsSource = _items;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("Dlg_FolderTitle") };
        if (dialog.ShowDialog(this) == true) { _folder = dialog.FolderName; FolderText.Text = _folder; }
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

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_running) { e.Cancel = true; Log(T("Ua_CloseWhileRunning")); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) { Log(T("Ua_NoneSelected")); return; }
        if (_folder.Length == 0) { Log(T("Ba_NoFolder")); ChooseFolder_Click(sender, e); if (_folder.Length == 0) return; }

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
        if (alsoBinary && item.IsMikroTik)
        {
            item.StateText = T("Ba_StateBinary");
            var binName = rscName.EndsWith(".rsc") ? rscName[..^4] + ".backup" : rscName + ".backup";
            var ok = await device.DownloadFullBackupAsync(_method, device.Model.SshPort, Path.Combine(_folder, binName), ct);
            if (!ok)
            {
                item.StateText = T("Ba_StateConfigOnly", device.LastError);
                Log(item.StateText);
                return true; // the config was saved – count it as a (partial) success
            }
        }
        item.StateText = T("Ba_StateDone");
        Log(T("Ba_StateDone") + " – " + rscName);
        return true;
    }

    private void SetUiRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        CloseButton.IsEnabled = !running;
        UpButton.IsEnabled = !running;
        DownButton.IsEnabled = !running;
        FolderButton.IsEnabled = !running;
        AlsoBinaryCheck.IsEnabled = !running;
        foreach (var item in _items) item.CanSelect = !running;
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        LogBox.ScrollToEnd();
    }
}
