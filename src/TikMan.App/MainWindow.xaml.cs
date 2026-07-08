using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DeviceViewModel> _devices = new();
    private readonly DispatcherTimer _pollTimer = new();
    private AppData _appData;

    public MainWindow() : this(new AppData()) { }

    public MainWindow(AppData appData)
    {
        InitializeComponent();
        _appData = appData;
        DeviceGrid.ItemsSource = _devices;
        _pollTimer.Tick += async (_, _) => await RefreshAllAsync(quiet: true);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var device in _appData.Devices)
            _devices.Add(new DeviceViewModel(device));

        SelectIntervalItem(_appData.PollIntervalSeconds);
        AutoRefreshCheck.IsChecked = _appData.AutoRefreshEnabled;
        ApplyTimerSettings();

        if (_devices.Count > 0)
            await RefreshAllAsync(quiet: false);
        else
            SetStatus(T("Msg_NoDevices"));
    }

    private void Window_Closing(object sender, CancelEventArgs e) => SaveAppData();

    private void SaveAppData()
    {
        _appData.Devices = _devices.Select(vm => vm.Model).ToList();
        _appData.PollIntervalSeconds = SelectedIntervalSeconds();
        _appData.AutoRefreshEnabled = AutoRefreshCheck.IsChecked == true;
        try { DeviceStore.Save(_appData); }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Msg_SaveConfigFailed", ex.Message),
                "TikMan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private DeviceViewModel? SelectedDevice => DeviceGrid.SelectedItem as DeviceViewModel;

    // ----- Settings -----

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_appData) { Owner = this };
        if (dialog.ShowDialog() == true) SaveAppData();
    }

    // ----- Queries / Monitoring -----

    private async Task RefreshAllAsync(bool quiet)
    {
        var targets = _devices.Where(d => d.Model.MonitoringEnabled || !quiet).ToList();
        if (targets.Count == 0) return;

        if (!quiet) SetStatus(T("Msg_Querying", targets.Count));
        await Task.WhenAll(targets.Select(d => d.RefreshAsync()));

        var online = targets.Count(d => d.Status == DeviceStatus.Online);
        var text = T("Msg_OnlineSummary", DateTime.Now.ToString("HH:mm:ss"), online, targets.Count);
        if (targets.Count(d => d.UpdateAvailable) is > 0 and var n)
            text += T("Msg_UpdatesAvailableSuffix", n);
        SetStatus(text);
    }

    private async void RefreshAll_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync(quiet: false);

    private void ApplyTimerSettings()
    {
        _pollTimer.Interval = TimeSpan.FromSeconds(SelectedIntervalSeconds());
        _pollTimer.IsEnabled = AutoRefreshCheck.IsChecked == true;
    }

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e) => ApplyTimerSettings();

    private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyTimerSettings();
    }

    private int SelectedIntervalSeconds() =>
        IntervalCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var s) ? s : 30;

    private void SelectIntervalItem(int seconds)
    {
        foreach (var item in IntervalCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == seconds.ToString())
            {
                IntervalCombo.SelectedItem = item;
                return;
            }
        }
        IntervalCombo.SelectedIndex = 1; // 30 s
    }

    // ----- Device management -----

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        var known = _devices.Select(d => d.Model).ToList();
        var dialog = new ScanWindow(known) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            foreach (var device in dialog.NewDevices)
            {
                var vm = new DeviceViewModel(device);
                _devices.Add(vm);
                _ = vm.RefreshAsync();
            }
            SaveAppData();
            SetStatus(T("Msg_DevicesAdded", dialog.NewDevices.Count));
        }
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DeviceEditWindow(null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } device)
        {
            var vm = new DeviceViewModel(device);
            _devices.Add(vm);
            SaveAppData();
            _ = vm.RefreshAsync();
        }
    }

    private void EditDevice_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm)
        {
            SetStatus(T("Msg_SelectDeviceFirst"));
            return;
        }
        var dialog = new DeviceEditWindow(vm.Model) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            vm.ResetClient();
            SaveAppData();
            _ = vm.RefreshAsync();
        }
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm)
        {
            SetStatus(T("Msg_SelectDeviceFirst"));
            return;
        }
        var answer = MessageBox.Show(this, T("Msg_RemoveConfirm", vm.Name, vm.Host),
            T("Msg_RemoveTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            _devices.Remove(vm);
            SaveAppData();
        }
    }

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedDevice is not null) EditDevice_Click(sender, e);
    }

    // ----- Logs -----

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm)
        {
            SetStatus(T("Msg_SelectDeviceFirst"));
            return;
        }
        int max = LogCountCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var n) ? n : 100;
        SetStatus(T("Msg_LoadingLogs", vm.Name));
        var ok = await vm.LoadLogsAsync(max);
        ApplyLogFilter();
        SetStatus(ok ? T("Msg_LogsLoaded", vm.Logs.Count, vm.Name) : T("Msg_LogsFailed", vm.Name, vm.LastError));
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLogFilter();
        SyncChannelCombo();
    }

    private bool _syncingChannel;

    /// <summary>Sets the channel dropdown to the device's current channel without letting
    /// the SelectionChanged handler trigger a channel switch.</summary>
    private void SyncChannelCombo()
    {
        _syncingChannel = true;
        try
        {
            var channel = SelectedDevice?.UpdateChannel ?? "";
            ChannelCombo.SelectedValue = channel.Length > 0 ? channel : null;
        }
        finally
        {
            _syncingChannel = false;
        }
    }

    private async void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingChannel) return;                       // programmatic synchronization, not a user action
        if (SelectedDevice is not { } vm) return;
        if (ChannelCombo.SelectedValue is not string channel || channel.Length == 0) return;
        if (channel == vm.UpdateChannel) return;           // already this channel

        SetStatus(T("Msg_SettingChannel", channel, vm.Name));
        var ok = await vm.SetChannelAsync(channel);
        if (!ok)
        {
            SetStatus(T("Msg_ChannelFailed", vm.Name, vm.LastError));
            SyncChannelCombo();                            // reset dropdown to the actual channel
            return;
        }
        var tail = vm.UpdateAvailable ? T("Msg_ChannelUpdateAvail", vm.LatestVersion) : T("Msg_ChannelNoUpdate");
        SetStatus(T("Msg_ChannelResult", vm.Name, vm.UpdateChannel, tail));
    }

    private void LogFilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyLogFilter();

    private void ApplyLogFilter()
    {
        if (SelectedDevice is not { } vm) return;
        var view = CollectionViewSource.GetDefaultView(vm.Logs);
        var filter = LogFilterBox.Text.Trim();
        view.Filter = filter.Length == 0
            ? null
            : obj => obj is LogEntry entry &&
                     (entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                      entry.Topics.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                      entry.Time.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Updates -----

    private async void CheckUpdatesAll_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0) return;
        SetStatus(T("Msg_CheckingUpdatesAll"));
        await Task.WhenAll(_devices.Select(d => d.CheckUpdateAsync()));
        var available = _devices.Count(d => d.UpdateAvailable);
        SetStatus(available > 0 ? T("Msg_UpdatesDoneSome", available) : T("Msg_UpdatesDoneNone"));
    }

    private async void CheckUpdateSingle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm) return;
        SetStatus(T("Msg_CheckingUpdateOne", vm.Name));
        await vm.CheckUpdateAsync();
        SetStatus(vm.UpdateAvailable
            ? T("Msg_UpdateAvailableOne", vm.Name, vm.LatestVersion)
            : T("Msg_NoUpdateOne", vm.Name, vm.UpdateStatusText.Length > 0 ? vm.UpdateStatusText : T("Msg_NoUpdateFallback")));
    }

    private void InstallUpdateSingle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm) return;
        OpenUpdateWindow(new List<DeviceViewModel> { vm });
    }

    // ----- Backups -----

    private async void BackupSingle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm)
        {
            SetStatus(T("Msg_SelectDeviceFirst"));
            return;
        }

        SetStatus(T("Msg_LoadingConfig", vm.Name));
        var result = await vm.DownloadConfigAsync();
        if (result is not { } data)
        {
            SetStatus(T("Msg_BackupFailed", vm.Name, vm.LastError));
            MessageBox.Show(this, T("Msg_BackupCantLoad", vm.LastError),
                T("Msg_BackupSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = T("Msg_BackupSaveTitle"),
            FileName = BackupNaming.SuggestFileName(data.Identity, vm.Board, vm.Host, DateTime.Now),
            Filter = T("Dlg_RscFilter"),
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, data.Config);
            SetStatus(T("Msg_BackupSaved", vm.Name, dialog.FileName));
        }
        catch (Exception ex)
        {
            SetStatus(T("Msg_SaveFailed", ex.Message));
            MessageBox.Show(this, T("Msg_SaveFileFailed", ex.Message),
                T("Msg_SaveFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BinaryBackupSingle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm)
        {
            SetStatus(T("Msg_SelectDeviceFirst"));
            return;
        }

        var suggested = BackupNaming.SuggestFileName(vm.Name, vm.Board, vm.Host, DateTime.Now)
            .Replace(".rsc", ".backup");
        var dialog = new SaveFileDialog
        {
            Title = T("Full_SaveTitle"),
            FileName = suggested,
            Filter = T("Full_Filter"),
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        SetStatus(T("Full_Running", vm.Name));
        var ok = await vm.DownloadFullBackupAsync(_appData.BackupMethod, _appData.SshPort, dialog.FileName);
        if (ok)
        {
            SetStatus(T("Full_Saved", vm.Name, dialog.FileName));
        }
        else
        {
            SetStatus(T("Full_Failed", vm.Name, vm.LastError));
            MessageBox.Show(this, T("Full_FailedBody", vm.LastError),
                T("Full_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BackupAll_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0)
        {
            SetStatus(T("Msg_NoDevicesConfigured"));
            return;
        }

        var folderDialog = new OpenFolderDialog { Title = T("Dlg_FolderTitle") };
        if (folderDialog.ShowDialog(this) != true) return;
        var folder = folderDialog.FolderName;

        var timestamp = DateTime.Now;
        var targets = _devices.ToList();
        SetStatus(T("Msg_BackingUpAll", targets.Count));

        int ok = 0;
        var failures = new List<string>();
        foreach (var vm in targets)
        {
            SetStatus(T("Msg_LoadingConfig", vm.Name));
            var result = await vm.DownloadConfigAsync();
            if (result is not { } data)
            {
                failures.Add($"{vm.Name} ({vm.Host}): {vm.LastError}");
                continue;
            }
            try
            {
                var fileName = BackupNaming.SuggestFileName(data.Identity, vm.Board, vm.Host, timestamp);
                File.WriteAllText(Path.Combine(folder, fileName), data.Config);
                ok++;
            }
            catch (Exception ex)
            {
                failures.Add($"{vm.Name} ({vm.Host}): {ex.Message}");
            }
        }

        SetStatus(T("Msg_BackupAllDone", ok, failures.Count, folder));
        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                T("Msg_BackupAllReport", ok, targets.Count, folder, string.Join("\n• ", failures)),
                T("Msg_BackupAllReportTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void InstallUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0)
        {
            SetStatus(T("Msg_NoDevicesConfigured"));
            return;
        }
        OpenUpdateWindow(_devices.ToList());
    }

    private void OpenUpdateWindow(List<DeviceViewModel> candidates)
    {
        var wasPolling = _pollTimer.IsEnabled;
        _pollTimer.IsEnabled = false; // don't interfere while updates are running
        try
        {
            var dialog = new UpdateAllWindow(candidates) { Owner = this };
            dialog.ShowDialog();
        }
        finally
        {
            _pollTimer.IsEnabled = wasPolling;
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
