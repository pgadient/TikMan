using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DeviceViewModel> _devices = new();
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private AppData _appData;

    public MainWindow() : this(new AppData()) { }

    public MainWindow(AppData appData)
    {
        InitializeComponent();
        _appData = appData;
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) Title = $"TikMan {v.Major}.{v.Minor}.{v.Build}";
        RouterOsClient.AllowInsecureCertificates = appData.DefaultIgnoreCertErrors;
        DeviceGrid.ItemsSource = _devices;
        _pollTimer.Tick += async (_, _) => await RefreshAllAsync(quiet: true);
        _logTimer.Tick += (_, _) => { if (SelectedDevice is { } vm) _ = LoadLogsAsync(vm, quiet: true); };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var device in _appData.Devices)
            _devices.Add(new DeviceViewModel(device));
        MarkGateways();
        CollectionViewSource.GetDefaultView(_devices).SortDescriptions.Add(
            new SortDescription(nameof(DeviceViewModel.Ipv4SortKey), ListSortDirection.Ascending)); // default sort by IPv4
        _ = LoadPublicIpAsync(); // fill the public-IP status field in the background

        InitSubnets();

        SelectIntervalItem(_appData.PollIntervalSeconds);
        AutoRefreshCheck.IsChecked = _appData.AutoRefreshEnabled;
        LogAutoRefreshCheck.IsChecked = _appData.LogAutoRefresh;
        _logTimer.IsEnabled = _appData.LogAutoRefresh;
        ApplyTimerSettings();

        if (_devices.Count > 0)
        {
            await RefreshAllAsync(quiet: false);
            _ = CheckUpdatesAsync(_devices.ToList());
        }

        // Discovery now runs automatically on startup (MNDP + IPv4 + IPv6), adding every host found.
        await RunDiscoveryAsync(auto: true);
    }

    private void Window_Closing(object sender, CancelEventArgs e) => SaveAppData();

    private void SaveAppData()
    {
        // Only persist the device list when the user opted in; otherwise devices are session-only.
        _appData.Devices = _appData.PersistDeviceList ? _devices.Select(vm => vm.Model).ToList() : new List<Device>();
        _appData.PollIntervalSeconds = SelectedIntervalSeconds();
        _appData.AutoRefreshEnabled = AutoRefreshCheck.IsChecked == true;
        _appData.LogAutoRefresh = LogAutoRefreshCheck.IsChecked == true;
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
        if (dialog.ShowDialog() != true) return;
        if (dialog.ResetRequested) ResetToDefaults();
        else
        {
            RouterOsClient.AllowInsecureCertificates = _appData.DefaultIgnoreCertErrors;
            SaveAppData();
        }
    }

    /// <summary>Wipes the config and returns the app to its first-start state.</summary>
    private void ResetToDefaults()
    {
        _pollTimer.IsEnabled = false;
        _logTimer.IsEnabled = false;
        DeviceStore.DeleteConfig();
        _devices.Clear();
        _appData = new AppData();
        RouterOsClient.AllowInsecureCertificates = _appData.DefaultIgnoreCertErrors;
        Instance.Apply(_appData.Language);
        SelectIntervalItem(_appData.PollIntervalSeconds);
        AutoRefreshCheck.IsChecked = _appData.AutoRefreshEnabled;
        LogAutoRefreshCheck.IsChecked = _appData.LogAutoRefresh;
        DeviceFilterBox.Text = "";
        ApplyTimerSettings();
        _logTimer.IsEnabled = _appData.LogAutoRefresh;
        SetStatus(T("Set_ResetDone"));
    }

    // ----- Queries / Monitoring -----

    private async Task RefreshAllAsync(bool quiet)
    {
        // Every device gets refreshed: monitored ones over REST/SSH (with data), the rest by a plain
        // ping so their status dot is green when reachable and red when they drop out.
        var targets = _devices.ToList();
        if (targets.Count == 0) return;

        if (!quiet) SetStatus(T("Msg_Querying", targets.Count));
        await Task.WhenAll(targets.Select(d => d.Model.MonitoringEnabled ? d.RefreshAsync() : d.RefreshReachabilityAsync()));

        // Retry TLS-failed devices over HTTP if the user allowed insecure HTTP login (else leave them).
        await ApplyHttpFallbackAsync(targets);

        var online = targets.Count(d => d.Status == DeviceStatus.Online);
        var text = T("Msg_OnlineSummary", DateTime.Now.ToString("HH:mm:ss"), online, targets.Count);
        if (targets.Count(d => d.UpdateAvailable) is > 0 and var n)
            text += T("Msg_UpdatesAvailableSuffix", n);
        SetStatus(text);
    }

    /// <summary>When "allow insecure HTTP login" is enabled in Settings, silently retries HTTPS
    /// devices whose TLS handshake failed over plain HTTP (credentials then travel in clear text)
    /// and re-queries them. No prompt – the user opts in once, then presses Refresh all.</summary>
    private async Task ApplyHttpFallbackAsync(IEnumerable<DeviceViewModel> devices)
    {
        if (!_appData.AllowHttpFallback) return;
        var candidates = devices.Where(d => d.Model.UseHttps && d.HadTlsError).ToList();
        if (candidates.Count == 0) return;

        foreach (var vm in candidates) vm.SwitchToHttp();
        SaveAppData();
        SetStatus(T("Msg_Querying", candidates.Count));
        await Task.WhenAll(candidates.Select(d => d.RefreshAsync()));
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

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DeviceEditWindow((Device?)null, _appData.DefaultIgnoreCertErrors) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } device)
        {
            var vm = new DeviceViewModel(device) { IsSelected = MainSelectAll.IsChecked == true };
            _devices.Add(vm);
            MarkGateways();
            SaveAppData();
            _ = RefreshAndCheckAsync(vm);
        }
    }

    /// <summary>Refreshes a freshly added device, then checks it for updates automatically.
    /// If the HTTPS handshake failed, offers the HTTP fallback and retries the update check.</summary>
    private async Task RefreshAndCheckAsync(DeviceViewModel vm)
    {
        if (await vm.RefreshAsync()) { await ApplyChannelAndCheckAsync(vm); return; }
        await ApplyHttpFallbackAsync(new[] { vm });
        if (vm.Status == DeviceStatus.Online) await ApplyChannelAndCheckAsync(vm);
    }

    /// <summary>Checks a device for updates on its effective channel — its own if set, otherwise the
    /// global default — switching the router's channel only when it actually differs (so read-only
    /// API users are never forced into a write).</summary>
    private async Task ApplyChannelAndCheckAsync(DeviceViewModel vm)
    {
        if (vm.Model.Vendor != DeviceVendor.MikroTik) return; // only RouterOS has channels/REST updates
        await vm.CheckUpdateAsync();
        var channel = vm.Model.UpdateChannel.Length > 0 ? vm.Model.UpdateChannel : _appData.DefaultUpdateChannel;
        if (channel.Length > 0 && !string.Equals(channel, vm.UpdateChannel, StringComparison.OrdinalIgnoreCase))
            await vm.SetChannelAsync(channel);
    }

    /// <summary>Flags devices whose host is the local default gateway (row shown orange).</summary>
    private void MarkGateways()
    {
        var gateways = NetworkInfo.GetDefaultGateways();
        foreach (var d in _devices) d.IsGateway = gateways.Contains(d.Host);
    }

    private void EditDevice_Click(object sender, RoutedEventArgs e)
    {
        // More than one device marked → edit them together (shared settings for all).
        var marked = _devices.Where(d => d.IsSelected).ToList();
        if (marked.Count > 1) { EditMultiple(marked); return; }

        var vm = SelectedDevice ?? marked.FirstOrDefault();
        if (vm is null)
        {
            SetStatus(T("Msg_SelectDeviceFirst"));
            return;
        }
        var dialog = new DeviceEditWindow(vm.Model) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            vm.ResetClient();
            MarkGateways();
            SaveAppData();
            _ = RefreshAndCheckAsync(vm);
        }
    }

    /// <summary>Edits the settings shared by several marked devices at once.</summary>
    private void EditMultiple(List<DeviceViewModel> vms)
    {
        var dialog = new DeviceEditWindow(vms.Select(v => v.Model).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var vm in vms) vm.ResetClient();
        MarkGateways();
        SaveAppData();
        foreach (var vm in vms) _ = RefreshAndCheckAsync(vm);
        SetStatus(T("Msg_DevicesUpdated", vms.Count));
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
        // Double-click on a cell copies its value to the clipboard.
        if (TryGetCellText(e.OriginalSource as DependencyObject, out var text))
            CopyToClipboard(text);
    }

    /// <summary>Single click on a web protocol badge (http/https) opens it in the browser.</summary>
    private void ProtocolBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && (sender as FrameworkElement)?.DataContext is ProtocolVm { IsWeb: true } proto)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(proto.Url) { UseShellExecute = true }); }
            catch { /* no browser / blocked */ }
            e.Handled = true;
        }
    }

    /// <summary>Walks up from the clicked element to its DataGridCell and returns the cell text
    /// (empty for non-text cells like the checkbox/status columns).</summary>
    private static bool TryGetCellText(DependencyObject? source, out string text)
    {
        text = "";
        TextBlock? tb = null;
        while (source is not null and not DataGridCell)
        {
            if (tb is null && source is TextBlock t) tb = t; // template columns wrap the text
            source = VisualTreeHelper.GetParent(source);
        }
        if (source is not DataGridCell cell) return false;   // clicked a header or empty space
        tb ??= cell.Content as TextBlock;                    // text columns expose the TextBlock directly
        if (tb is not null && tb.Text.Length > 0) { text = tb.Text; return true; }
        return false;
    }

    /// <summary>Copies text to the clipboard and confirms in the status bar.</summary>
    private void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); SetStatus(T("Msg_Copied")); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard is briefly locked by another app – ignore
        }
    }

    /// <summary>Copies the full IEEE vendor record (name + address) for the row's MAC.</summary>
    private void CopyVendorFull_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not DeviceViewModel vm) return;
        var entry = OuiLookup.GetFullEntry(vm.Model.MacAddress);
        if (entry.Length > 0) CopyToClipboard(entry);
    }

    /// <summary>Shows the full IEEE OUI record (name + address) for the row's MAC in a copyable popup.</summary>
    private void VendorInfo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceViewModel vm) return;
        var entry = OuiLookup.GetFullEntry(vm.Model.MacAddress);
        if (entry.Length == 0) return;
        new VendorInfoWindow(entry) { Owner = this }.ShowDialog();
    }

    // ----- Logs -----

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm)
        {
            SetStatus(T("Msg_SelectDeviceFirst"));
            return;
        }
        await LoadLogsAsync(vm);
    }

    private async Task LoadLogsAsync(DeviceViewModel vm, bool quiet = false)
    {
        int max = LogCountCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var n) ? n : 100;
        if (!quiet) SetStatus(T("Msg_LoadingLogs", vm.Name));
        var ok = await vm.LoadLogsAsync(max);
        ApplyLogFilter();
        if (!quiet)
            SetStatus(ok ? T("Msg_LogsLoaded", vm.Logs.Count, vm.Name) : T("Msg_LogsFailed", vm.Name, vm.LastError));
    }

    private void LogAutoRefresh_Changed(object sender, RoutedEventArgs e) =>
        _logTimer.IsEnabled = LogAutoRefreshCheck.IsChecked == true;

    // ----- Main list filter -----

    private void DeviceFilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyDeviceFilter();

    private void ApplyDeviceFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_devices);
        var tokens = DeviceFilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        view.Filter = tokens.Length == 0
            ? null
            : obj => obj is DeviceViewModel d && DeviceMatchesFilter(d, tokens);
    }

    private static bool DeviceMatchesFilter(DeviceViewModel d, string[] tokens)
    {
        var haystack = string.Join(" ",
            d.Name, d.AllAddressesText, d.TransportDisplay, d.MacVendor, d.IdentifiedVendor, d.DeviceType, d.ModelDisplay, d.Version, d.LatestWithChannel,
            d.CpuText, d.MemoryText, d.Uptime, d.UpdateChannel, d.StatusText, d.LastError);
        return tokens.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLogFilter();
        if (SelectedDevice is { } vm)
        {
            // Initial log load on first selection of a device (Refresh logs reloads later).
            if (vm.Logs.Count == 0) _ = LoadLogsAsync(vm);
            _ = vm.LoadAvailableUpdatesAsync(); // fill the "Available updates" tab
            _ = vm.LoadSharesAsync();           // fill the SMB shares in the Details tab
        }
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

    /// <summary>Checks the given devices for firmware updates. Runs automatically after a device
    /// is added and once on startup – there is no manual "Check updates" button anymore.</summary>
    private async Task CheckUpdatesAsync(IReadOnlyList<DeviceViewModel> targets)
    {
        if (targets.Count == 0) return;
        await Task.WhenAll(targets.Select(ApplyChannelAndCheckAsync));
        var available = targets.Count(d => d.UpdateAvailable);
        if (available > 0) SetStatus(T("Msg_UpdatesDoneSome", available));
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
        var targets = _devices.Where(d => d.IsSelected).ToList();
        if (targets.Count == 0)
        {
            SetStatus(T("Msg_NoDevicesMarked"));
            return;
        }

        var folderDialog = new OpenFolderDialog { Title = T("Dlg_FolderTitle") };
        if (folderDialog.ShowDialog(this) != true) return;
        var folder = folderDialog.FolderName;

        var timestamp = DateTime.Now;
        SetStatus(T("Msg_BackingUpAll", targets.Count));
        BeginProgress(targets.Count);

        int ok = 0;
        var failures = new List<string>();
        foreach (var vm in targets)
        {
            SetStatus(T("Msg_LoadingConfig", vm.Name));
            var result = await vm.DownloadConfigAsync();
            if (result is { } data)
            {
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
            else
            {
                failures.Add($"{vm.Name} ({vm.Host}): {vm.LastError}");
            }
            StepProgress();
        }
        EndProgress();

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
        var targets = _devices.Where(d => d.IsSelected).ToList();
        if (targets.Count == 0)
        {
            SetStatus(T("Msg_NoDevicesMarked"));
            return;
        }
        OpenUpdateWindow(targets);
    }

    // ----- Selection / progress -----

    private void MainSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var value = MainSelectAll.IsChecked == true;
        foreach (var d in _devices) d.IsSelected = value;
    }

    private void BeginProgress(int max)
    {
        MainProgress.Minimum = 0;
        MainProgress.Maximum = Math.Max(1, max);
        MainProgress.Value = 0;
        MainProgress.Visibility = Visibility.Visible;
    }

    private void StepProgress()
    {
        if (MainProgress.Value < MainProgress.Maximum) MainProgress.Value += 1;
    }

    private void EndProgress()
    {
        MainProgress.Visibility = Visibility.Collapsed;
        MainProgress.Value = 0;
    }

    private void OpenUpdateWindow(List<DeviceViewModel> candidates)
    {
        var wasPolling = _pollTimer.IsEnabled;
        _pollTimer.IsEnabled = false; // don't interfere while updates are running
        try
        {
            var dialog = new UpdateAllWindow(candidates) { Owner = this };
            dialog.ShowDialog();
            ApplyUpdateOrder(dialog.OrderedDevices); // persist any reordering done in the dialog
        }
        finally
        {
            _pollTimer.IsEnabled = wasPolling;
        }
    }

    /// <summary>Reorders the main device list so the given devices appear in this order
    /// (at the positions they occupied), then persists the new order.</summary>
    private void ApplyUpdateOrder(IReadOnlyList<DeviceViewModel> orderedSubset)
    {
        var subset = new HashSet<DeviceViewModel>(orderedSubset);
        var queue = new Queue<DeviceViewModel>(orderedSubset);
        var target = _devices.Select(d => subset.Contains(d) ? queue.Dequeue() : d).ToList();
        for (int i = 0; i < target.Count; i++)
        {
            int cur = _devices.IndexOf(target[i]);
            if (cur != i) _devices.Move(cur, i);
        }
        SaveAppData();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Builds a diagnostic report and opens a pre-filled problem e-mail to the support
    /// address – Outlook Classic with the log attached, else the default mail app with it inline.</summary>
    private void ReportProblem_Click(object sender, RoutedEventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var version = v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        var (file, text) = ProblemReporter.BuildReport(version);
        var subject = T("Rep_Subject", version);

        // Prefer Outlook Classic (file attachment) unless the user forces the mailto fallback.
        if (!_appData.ForceMailFallback && ProblemReporter.TryOutlookClassic(subject, T("Rep_Body"), file))
        {
            SetStatus(T("Rep_Opened"));
            return;
        }

        // Fallback: default mail via mailto – no attachment, so put the (capped) log inline.
        var inline = text.Length > 1200 ? text[..1200] + $"\n…\n({T("Rep_FullLog")}: {file})" : text;
        if (ProblemReporter.TryMailto(subject, T("Rep_Body") + "\n\n" + inline))
            SetStatus(file.Length > 0 ? T("Rep_OpenedFallback", file) : T("Rep_Opened"));
        else
            SetStatus(T("Rep_Failed", file));
    }

    /// <summary>Opens an SMB share (\\host\share) in Windows Explorer.</summary>
    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmbShareVm share })
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{share.UncPath}\"") { UseShellExecute = true });
            }
            catch { /* Explorer not available / path gone */ }
        }
    }

    /// <summary>Opens the TP-Link/Omada firmware download page for the selected switch in the browser.</summary>
    private void OpenFirmwarePage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { FirmwarePageUrl: { Length: > 0 } url }) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked */ }
    }

    // ----- Public IP -----

    private (string V4, string V6) _publicIp;

    /// <summary>Looks up the public IPv4/IPv6 address and shows it (clickable) in the status bar.</summary>
    private async Task LoadPublicIpAsync()
    {
        var ip = await PublicIpClient.GetAsync();
        _publicIp = (ip.V4, ip.V6);
        var v4 = ip.V4.Length > 0 ? ip.V4 : T("Val_Na");
        var v6 = ip.V6.Length > 0 ? ip.V6 : T("Val_Na");
        PublicIpText.Text = $"🌐 IPv4 {v4}  ·  IPv6 {v6}";
    }

    private List<string> PublicIpParts()
    {
        var parts = new List<string>();
        if (_publicIp.V4.Length > 0) parts.Add(_publicIp.V4);
        if (_publicIp.V6.Length > 0) parts.Add(_publicIp.V6);
        return parts;
    }

    private void PublicIp_Click(object sender, MouseButtonEventArgs e)
    {
        var parts = PublicIpParts();
        if (parts.Count > 0) CopyToClipboard(string.Join(Environment.NewLine, parts));
    }
}
