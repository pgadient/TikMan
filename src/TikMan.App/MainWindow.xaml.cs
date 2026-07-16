using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly ObservableCollection<Ipv6RowVm> _v6Rows = new();
    private bool _v6Mode;
    private int _addressColumnIndex; // display index of the first address column (IPv4 in the v4 view)
    private static readonly Brush Ipv6GroupBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xF2, 0xFA)); // ice blue
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _filterDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AppData _appData;

    public MainWindow() : this(new AppData()) { }

    public MainWindow(AppData appData)
    {
        InitializeComponent();
        _appData = appData;
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) Title = $"TikMan {v.Major}.{v.Minor}.{v.Build}";
        RouterOsClient.AllowInsecureCertificates = appData.DefaultIgnoreCertErrors;
        RouterOsClient.AllowHttpFallback = appData.AllowHttpFallback;
        DeviceGrid.ItemsSource = _devices;
        if (Ipv6GroupBrush.CanFreeze) Ipv6GroupBrush.Freeze();
        // The v4 view hides IPv6-only devices live (HasIpv4 flips when a MAC-match adds an IPv4).
        if (CollectionViewSource.GetDefaultView(_devices) is ListCollectionView lcv)
        {
            lcv.IsLiveFiltering = true;
            lcv.LiveFilteringProperties.Add(nameof(DeviceViewModel.HasIpv4));
        }
        _devices.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
                foreach (DeviceViewModel vm in args.NewItems)
                    vm.PropertyChanged += Device_PropertyChangedForV6;
            if (_v6Mode) BuildV6Rows();
            UpdateDeviceCount();
        };
        _pollTimer.Tick += async (_, _) => await RefreshAllAsync(quiet: true);
        _logTimer.Tick += (_, _) => { if (SelectedDevice is { } vm) _ = LoadLogsAsync(vm, quiet: true); };
        _filterDebounce.Tick += FilterDebounce_Tick;
        UpdatesView.RunningChanged += UpdatesView_RunningChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var device in _appData.Devices)
            _devices.Add(new DeviceViewModel(device));
        MarkGateways();
        CollectionViewSource.GetDefaultView(_devices).SortDescriptions.Add(
            new SortDescription(nameof(DeviceViewModel.Ipv4SortKey), ListSortDirection.Ascending)); // default sort by IPv4
        _addressColumnIndex = Ipv4Column.DisplayIndex;
        ApplyDeviceFilter(); // the v4 view only lists devices that have an IPv4
        if (_appData.PersistDeviceList) ApplyColumnLayout(); // restore saved order/width/sort
        _ = LoadPublicIpAsync(); // fill the public-IP status field in the background

        InitSubnets();
        ApplyCoffeeButton();
        foreach (var vm in _devices) ApplyDefaultExpansion(vm); // persisted devices

        if (_appData.ShowIpv6View) AddressTabs.SelectedIndex = 1; // restore the last address view
        SimpleModeCheck.IsChecked = _appData.SimpleScanMode;
        ApplyContactButtons();
        ApplyListInfo();
        // A quiet bottom-bar hint when Npcap is missing, so ZON discovery's absence is explained.
        NpcapWarnText.Visibility = ZdpScanner.IsAvailable() ? Visibility.Collapsed : Visibility.Visible;
        UpdateDeviceCount();
        SelectIntervalItem(_appData.PollIntervalSeconds);
        AutoRefreshCheck.IsChecked = _appData.AutoRefreshEnabled;
        LogAutoRefreshCheck.IsChecked = _appData.LogAutoRefresh;
        _logTimer.IsEnabled = _appData.LogAutoRefresh;
        ApplyTimerSettings();

        // The app's own update check runs *first*, before we touch the network. It is one small HTTPS
        // call, and everything after it is work we'd throw away: an update restarts the app, so a scan
        // started underneath it is time the user waits for nothing.
        if (_appData.CheckForUpdates && await CheckForUpdateAsync()) return; // updating → we're restarting

        // Startup scan off + a stored device list means exactly that: touch nothing, show the list as
        // it was saved. Refreshing "just the monitoring" here still queried every device, rewrote their
        // state and re-ran the map behind the user's back – the very thing the setting turns off. The
        // devices stay grey (never checked this session) until a scan or a refresh says otherwise.
        if (!_appData.NoInitialScan)
        {
            if (_devices.Count > 0)
            {
                await RefreshAllAsync(quiet: false);
                _ = CheckUpdatesAsync(_devices.ToList());
            }
            // Discovery runs automatically on startup, adding every host found.
            await RunDiscoveryAsync(auto: true);
        }
        else { UpdateNoLoginBanner(); SetStatus(_devices.Count > 0 ? T("Status_RestoredUnchecked") : T("Status_Ready")); }

        if (_appData.WebServerAutoStart) StartWebServer(announce: false);
    }

    /// <summary>This build's version (Major.Minor.Build) and the running exe's filename – what the
    /// updater needs to compare and to pick the matching asset.</summary>
    internal static (Version Version, string ExeName) CurrentBuild()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        var exe = "";
        try { exe = Path.GetFileName(Environment.ProcessPath ?? ""); } catch { /* keep empty */ }
        return (new Version(v.Major, v.Minor, v.Build), exe);
    }

    /// <summary>On startup: if GitHub has a newer release for this build's variant, offer to update.
    /// True when we are updating and the app is on its way out – the caller must then start nothing
    /// else.</summary>
    private async Task<bool> CheckForUpdateAsync()
    {
        var (current, exeName) = CurrentBuild();
        if (exeName.Length == 0) return false;
        SetStatus(T("Upd_Checking"));
        var update = await AppUpdater.CheckAsync(current, exeName);
        SetStatus(T("Status_Ready"));
        if (update is null) return false;

        var answer = MessageBox.Show(this,
            T("Upd_AvailableBody", $"{update.Version} „{update.ReleaseName}“", current.ToString()),
            T("Upd_AvailableTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
        return answer == MessageBoxResult.Yes && await PerformUpdateAsync(update);
    }

    /// <summary>Downloads the update next to the current exe, launches it (telling it to delete this
    /// one once we exit) and quits. Best-effort – any failure leaves the running version in place and
    /// returns false, so the caller carries on as normal.</summary>
    internal async Task<bool> PerformUpdateAsync(AppUpdater.Available update)
    {
        string exePath;
        try { exePath = Environment.ProcessPath ?? ""; } catch { return false; }
        if (exePath.Length == 0) return false;

        // A modal window for the download: the app is being replaced under the user's feet, so letting
        // them click around the list meanwhile promises an interactivity we're about to take away – and
        // a 65 MB download behind a one-line status just looks like a hang.
        var dlg = new UpdateProgressWindow(update.Version.ToString(), update.ReleaseName) { Owner = this };
        var progress = new Progress<double>(dlg.SetProgress);
        SetStatus(T("Upd_Downloading", update.Version.ToString()));

        var dir = Path.GetDirectoryName(exePath) ?? "";
        var download = AppUpdater.DownloadAsync(update, dir, progress);
        // Close the dialog when the download ends, whichever way it ends – ShowDialog() below only
        // returns then, and the window refuses a manual close.
        _ = download.ContinueWith(_ => dlg.CloseWhenReady(), TaskScheduler.FromCurrentSynchronizationContext());
        dlg.ShowDialog();

        var newExe = await download;
        if (newExe is null || string.Equals(newExe, exePath, StringComparison.OrdinalIgnoreCase))
        { SetStatus(T("Upd_Failed")); return false; } // failed, or same filename (can't swap in place)

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(newExe)
            {
                UseShellExecute = true,
                Arguments = $"--replaced \"{exePath}\"",
            });
            Application.Current.Shutdown(); // the new instance deletes this exe once we've exited
            return true;
        }
        catch (Exception) { SetStatus(T("Upd_Failed")); return false; }
    }

    private void Window_Closing(object sender, CancelEventArgs e) { StopWebServer(); SaveAppData(); }

    private void SaveAppData()
    {
        // Only persist the device list + column layout when the user opted in.
        _appData.Devices = _appData.PersistDeviceList ? _devices.Select(vm => vm.Model).ToList() : new List<Device>();
        if (_appData.PersistDeviceList) CaptureColumnLayout(); else _appData.ColumnLayout = new();
        _appData.PollIntervalSeconds = SelectedIntervalSeconds();
        _appData.AutoRefreshEnabled = AutoRefreshCheck.IsChecked == true;
        _appData.LogAutoRefresh = LogAutoRefreshCheck.IsChecked == true;
        _appData.ShowIpv6View = _v6Mode;
        try { DeviceStore.Save(_appData); }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Msg_SaveConfigFailed", ex.Message),
                "TikMan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Captures the current column order/width and the active sort into the config.</summary>
    private void CaptureColumnLayout()
    {
        _appData.ColumnLayout = DeviceGrid.Columns
            .Select(c => new ColumnState { Width = c.ActualWidth, DisplayIndex = c.DisplayIndex })
            .ToList();
        var sorted = DeviceGrid.Columns.FirstOrDefault(c => c.SortDirection is not null);
        _appData.SortColumn = sorted is null ? -1 : DeviceGrid.Columns.IndexOf(sorted);
        _appData.SortDescending = sorted?.SortDirection == ListSortDirection.Descending;
    }

    /// <summary>Restores a saved column order/width and sort (only if the counts still match).</summary>
    private void ApplyColumnLayout()
    {
        var saved = _appData.ColumnLayout;
        if (saved.Count != DeviceGrid.Columns.Count) return;
        // Apply widths first, then display indices in target order so the permutation stays valid.
        for (int i = 0; i < saved.Count; i++)
            if (saved[i].Width > 20) DeviceGrid.Columns[i].Width = new DataGridLength(saved[i].Width);
        foreach (var (col, _) in DeviceGrid.Columns
                     .Select((c, i) => (c, saved[i].DisplayIndex)).OrderBy(x => x.Item2))
            col.DisplayIndex = Math.Clamp(saved[DeviceGrid.Columns.IndexOf(col)].DisplayIndex, 0, DeviceGrid.Columns.Count - 1);

        if (_appData.SortColumn >= 0 && _appData.SortColumn < DeviceGrid.Columns.Count)
        {
            var col = DeviceGrid.Columns[_appData.SortColumn];
            var dir = _appData.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            col.SortDirection = dir;
            if (col.SortMemberPath is { Length: > 0 } path)
            {
                var view = CollectionViewSource.GetDefaultView(DeviceGrid.ItemsSource);
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(path, dir));
            }
        }
    }

    private DeviceViewModel? SelectedDevice => RowDevice(DeviceGrid.SelectedItem);

    /// <summary>Unwraps a grid row (device row of the v4 view, address row of the v6 view).</summary>
    private static DeviceViewModel? RowDevice(object? row) => row switch
    {
        DeviceViewModel d => d,
        Ipv6RowVm r => r.Device,
        _ => null,
    };

    /// <summary>The devices the user has highlighted in the list (multi-select via Ctrl/Shift-click
    /// or by dragging with the mouse).</summary>
    private List<DeviceViewModel> MarkedDevices() =>
        DeviceGrid.SelectedItems.Cast<object>().Select(RowDevice).OfType<DeviceViewModel>().Distinct().ToList();

    /// <summary>Refreshes the "N devices · X IPv4 · Y IPv6" counter under the list. The total is shown
    /// too because a device may have both or only IPv6, so IPv4+IPv6 don't add up to it.</summary>
    /// <summary>Shows the "set a login" banner while devices are present but none has credentials –
    /// that is when the map and the readouts are poorest. Hidden the moment any device has a login (or
    /// there are no devices yet). Today only MikroTik is read in full; the wording says so.</summary>
    private void UpdateNoLoginBanner() =>
        NoLoginBanner.Visibility = _devices.Count > 0 && !_devices.Any(d => d.HasCredentials)
            ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateDeviceCount()
    {
        UpdateNoLoginBanner();
        var text = T("Cnt_Devices", _devices.Count, _devices.Count(d => d.HasIpv4), _devices.Count(d => d.HasIpv6));

        // With a filter active, how many rows survive it – so the user sees the reach of their query.
        if (DeviceGrid.ItemsSource is not null && DeviceFilterBox.Text.Trim().Length > 0)
        {
            int shown = CollectionViewSource.GetDefaultView(DeviceGrid.ItemsSource).Cast<object>().Count();
            text += "   " + T("Cnt_Shown", shown);
        }

        // How many devices are highlighted (distinct, since a v6 device spans several address rows).
        int selected = DeviceGrid.SelectedItems.Cast<object>().Select(RowDevice)
            .Where(d => d is not null).Distinct().Count();
        if (selected > 0) text += "   " + T("Cnt_Selected", selected);

        DeviceCountText.Text = text;
    }

    // ----- Drag selection: press a row and drag to mark a range -----
    private int _dragAnchor = -1;

    private void DeviceGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Don't start a drag from an interactive cell element (expander, badge, checkbox).
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) { _dragAnchor = -1; return; }
        _dragAnchor = RowIndexFrom(e.OriginalSource as DependencyObject);
    }

    private void DeviceGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragAnchor < 0) return;
        var cur = RowIndexFrom(e.OriginalSource as DependencyObject);
        if (cur < 0 || cur == _dragAnchor) return;
        int lo = Math.Min(_dragAnchor, cur), hi = Math.Max(_dragAnchor, cur);
        DeviceGrid.SelectedItems.Clear();
        for (int i = lo; i <= hi && i < DeviceGrid.Items.Count; i++)
            DeviceGrid.SelectedItems.Add(DeviceGrid.Items[i]);
        e.Handled = true;
    }

    private void DeviceGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragAnchor = -1;

    /// <summary>Row index of the DataGridRow under a clicked element, or -1.</summary>
    private static int RowIndexFrom(DependencyObject? d)
    {
        while (d is not null and not DataGridRow) d = VisualTreeHelper.GetParent(d);
        return d is DataGridRow row ? row.GetIndex() : -1;
    }

    // ----- Settings -----

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var oldCoffee = _appData.CoffeeButton;
        var oldExpand = _appData.ExpandRowsByDefault;
        var oldPersist = _appData.PersistDeviceList;
        var dialog = new SettingsWindow(_appData) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.UpdateRequested is { } requestedUpdate) { _ = PerformUpdateAsync(requestedUpdate); return; }
        if (dialog.ResetRequested) ResetToDefaults();
        else
        {
            RouterOsClient.AllowInsecureCertificates = _appData.DefaultIgnoreCertErrors;
            RouterOsClient.AllowHttpFallback = _appData.AllowHttpFallback;
            ApplyCoffeeButton();
            ApplyContactButtons();   // the view toggles live in the settings dialog now
            ApplyListInfo();
            if (_appData.PersistDeviceList && !oldPersist)
                MessageBox.Show(this, T("Set_PersistWarn"), T("Set_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_appData.ExpandRowsByDefault != oldExpand)
                foreach (var vm in _devices)
                {
                    if (_appData.ExpandRowsByDefault) ApplyDefaultExpansion(vm);
                    else vm.IsExpanded = false;
                }
            SaveAppData();
            if (_appData.CoffeeButton == "off" && oldCoffee != "off")
                MessageBox.Show(this, T("Coffee_OffMsg"), T("Coffee_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Applies the coffee-button preference (normal size, small ☕ only, or hidden).</summary>
    private void ApplyCoffeeButton()
    {
        switch (_appData.CoffeeButton)
        {
            case "off":
                CoffeeButton.Visibility = Visibility.Collapsed;
                break;
            case "small":
                CoffeeButton.Visibility = Visibility.Visible;
                CoffeeButton.Content = "☕";
                CoffeeButton.Padding = new Thickness(6, 5, 6, 5);
                break;
            default:
                CoffeeButton.Visibility = Visibility.Visible;
                CoffeeButton.Content = T("Tb_BuyCoffee");
                CoffeeButton.Padding = new Thickness(10, 5, 10, 5); // ToolButton dimensions
                break;
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
        ApplyCoffeeButton();
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

    /// <summary>View menu: show or hide the ⓘ list-tips icon above the device list.</summary>
    /// <summary>Bottom-bar Npcap warning: reminds that Npcap must be installed with WinPcap
    /// API-compatible mode (mandatory for ZON), then opens the Npcap download page.</summary>
    private void NpcapWarn_Click(object sender, MouseButtonEventArgs e)
    {
        MessageBox.Show(this, T("Npcap_InstallHint"), "Npcap", MessageBoxButton.OK, MessageBoxImage.Information);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true }); }
        catch { /* no browser */ }
    }

    private void ApplyListInfo() =>
        ListInfoIcon.Visibility = _appData.ShowListInfo ? Visibility.Visible : Visibility.Collapsed;

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
            var vm = new DeviceViewModel(device);
            _devices.Add(vm);
            MarkGateways();
            SaveAppData();
            _ = RefreshAndProbeAsync(vm);
        }
    }

    /// <summary>After credentials changed: monitors the device and re-probes the various services
    /// (SSH info, web fingerprint, WMI, SNMP, …) so model/serial/firmware can now be read with the
    /// new login – the user never has to pick a port or protocol.</summary>
    private async Task RefreshAndProbeAsync(DeviceViewModel vm)
    {
        await RefreshAndCheckAsync(vm);
        try { await EnrichDetailsAsync(vm); } catch { /* best effort */ }
    }

    /// <summary>Fresh credentials unlock information everywhere, so nobody should have to remember to
    /// rescan by hand: the devices are re-probed and a fresh discovery runs. The topology evidence is
    /// dropped so the pass that finishes rebuilds the maps with what the new login reveals – the maps
    /// are only ever refreshed between scans, so this doesn't touch them mid-scan.</summary>
    private async Task AfterCredentialsChangedAsync(IReadOnlyList<DeviceViewModel> vms)
    {
        UpdateNoLoginBanner();   // a login was just added – the banner can go
        await Task.WhenAll(vms.Select(RefreshAndProbeAsync));
        InvalidateTopologyEvidence();          // force a fresh forwarding-table read on the next build
        UpdateTopoScanBanner();
        // Either way the map refreshes only when a scan finishes: start one if none is running,
        // otherwise the already-running pass rebuilds it in its own completion handler.
        if (!_scanning) _ = RunDiscoveryAsync(auto: true);
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

    /// <summary>View menu: show or hide the coloured contact buttons (report / request feature).</summary>
    private void ApplyContactButtons()
    {
        // Only the report/feature buttons – the coffee button has its own setting.
        var vis = _appData.ShowContactButtons ? Visibility.Visible : Visibility.Collapsed;
        ReportProblemButton.Visibility = vis;
        RequestFeatureButton.Visibility = vis;
    }

    /// <summary>Toolbar/context menu: set credentials for the marked devices (or the selected row
    /// when nothing is marked). One device → single editor; several → the same values are applied
    /// 1:1 to every marked device.</summary>
    private void SetLogin_Click(object sender, RoutedEventArgs e)
    {
        var targets = MarkedDevices();
        if (targets.Count == 0 && SelectedDevice is { } sel) targets.Add(sel);
        if (targets.Count == 0)
        {
            MessageBox.Show(this, T("Msg_SelectDeviceFirst"), T("Tb_SetCreds"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (targets.Count == 1)
        {
            var dialog = new DeviceEditWindow(targets[0].Model) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            targets[0].ResetClient();
            MarkGateways();
            SaveAppData();
            _ = AfterCredentialsChangedAsync(new[] { targets[0] });
        }
        else EditMultiple(targets);
    }

    /// <summary>Context menu: open an SSH terminal to the selected device.</summary>
    private void ContextSsh_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm) { SetStatus(T("Msg_SelectDeviceFirst")); return; }
        var host = _v6Mode ? vm.Ipv6List.FirstOrDefault() ?? vm.Ipv4Address : vm.Ipv4Address;
        if (host.Length == 0) host = vm.Host;
        LaunchSshTerminal(host.Trim('[', ']'), vm);
    }

    /// <summary>Context menu: open the device's SSH/SFTP session in WinSCP (path set in settings).</summary>
    private void OpenWinScp_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } vm) { SetStatus(T("Msg_SelectDeviceFirst")); return; }

        var winscp = _appData.WinScpPath.Trim();
        if (winscp.Length == 0 || !File.Exists(winscp))
        {
            MessageBox.Show(this, T("Wsc_NoPath"), "WinSCP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var host = (_v6Mode ? vm.Ipv6List.FirstOrDefault() ?? vm.Ipv4Address : vm.Ipv4Address);
        if (host.Length == 0) host = vm.Host;
        host = host.Trim('[', ']');
        var user = vm.Model.Username.Trim();
        var port = vm.Model.SshPort;
        var password = CredentialProtector.Unprotect(vm.Model.EncryptedPassword);

        // sftp://user[:password]@host[:port]/  – the stored credential opens the session directly.
        var auth = Uri.EscapeDataString(user);
        if (password.Length > 0) auth += ":" + Uri.EscapeDataString(password);
        if (auth.Length > 0) auth += "@";
        var session = $"sftp://{auth}{host}{(port != 22 ? $":{port}" : "")}/";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(winscp, $"\"{session}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"WinSCP: {ex.Message}"); }
    }

    /// <summary>Edits the settings shared by several marked devices at once.</summary>
    private void EditMultiple(List<DeviceViewModel> vms)
    {
        var dialog = new DeviceEditWindow(vms.Select(v => v.Model).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var vm in vms) vm.ResetClient();
        MarkGateways();
        SaveAppData();
        _ = AfterCredentialsChangedAsync(vms);
        SetStatus(T("Msg_DevicesUpdated", vms.Count));
    }

    /// <summary>Toolbar "Clear": empties the whole device list (both address views) after a confirm.</summary>
    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0) return;
        var answer = MessageBox.Show(this, T("Msg_ClearConfirm", _devices.Count),
            T("Tb_Clear"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        _devices.Clear();
        _v6Rows.Clear();
        SaveAppData();
        SetStatus(T("Status_Ready"));
    }

    private void ExportIpv4Csv_Click(object sender, RoutedEventArgs e) => ExportDevices(ipv6: false, html: false);
    private void ExportIpv4Html_Click(object sender, RoutedEventArgs e) => ExportDevices(ipv6: false, html: true);
    private void ExportIpv6Csv_Click(object sender, RoutedEventArgs e) => ExportDevices(ipv6: true, html: false);
    private void ExportIpv6Html_Click(object sender, RoutedEventArgs e) => ExportDevices(ipv6: true, html: true);

    // Context menu on the list itself: export whatever list is currently in front of the user – the
    // IPv4 view exports IPv4, the IPv6 view IPv6, with no family picker in between.
    private void ExportListCsv_Click(object sender, RoutedEventArgs e) => ExportDevices(_v6Mode, html: false);
    private void ExportListHtml_Click(object sender, RoutedEventArgs e) => ExportDevices(_v6Mode, html: true);

    /// <summary>Saves the IPv4 or IPv6 device list as CSV or a self-contained HTML table.</summary>
    private void ExportDevices(bool ipv6, bool html)
    {
        var devices = _devices.Where(d => ipv6 ? d.HasIpv6 : d.HasIpv4).ToList();
        if (devices.Count == 0) { SetStatus(T("Msg_NothingToExport")); return; }

        var family = ipv6 ? "ipv6" : "ipv4";
        var ext = html ? "html" : "csv";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = html ? "HTML (*.html)|*.html" : "CSV (*.csv)|*.csv",
            FileName = $"tikman-{family}-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var title = $"TikMan {(ipv6 ? "IPv6" : "IPv4")} devices";
            var content = html
                ? DeviceExporter.ToHtml(devices, ipv6, title, DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                : DeviceExporter.ToCsv(devices, ipv6);
            System.IO.File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(true));
            SetStatus(T("Msg_Exported", devices.Count, dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("Menu_Export"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Context menu: send a Wake-on-LAN magic packet to the marked (or selected) devices'
    /// MAC addresses.</summary>
    private void WakeDevice_Click(object sender, RoutedEventArgs e)
    {
        var targets = MarkedDevices();
        if (targets.Count == 0 && SelectedDevice is { } sel) targets = new List<DeviceViewModel> { sel };
        if (targets.Count == 0) { SetStatus(T("Msg_SelectDeviceFirst")); return; }

        int sent = 0;
        foreach (var vm in targets)
            if (vm.Model.MacAddress.Length > 0 && WakeOnLan.Send(vm.Model.MacAddress)) sent++;
        SetStatus(sent > 0 ? T("Wol_SentCount", sent) : T("Wol_NoMac", targets[0].Host));
    }

    /// <summary>Toolbar: wake a device by a manually entered MAC or IP – works even when the device is
    /// offline (an IP is resolved to its MAC via the OS ARP table if it's still cached).</summary>
    private void WakeManual_Click(object sender, RoutedEventArgs e)
    {
        var input = InputPrompt.Show(this, T("Wol_Title"), T("Wol_Prompt"));
        if (string.IsNullOrWhiteSpace(input)) return;

        var mac = input.Trim();
        if (WakeOnLan.ParseMac(mac) is null && System.Net.IPAddress.TryParse(mac, out var ip))
        {
            mac = SubnetScanner.ResolveMacAddress(ip);
            if (mac.Length == 0) { SetStatus(T("Wol_NoMac", input)); return; }
        }
        SetStatus(WakeOnLan.Send(mac) ? T("Wol_Sent", mac) : T("Wol_Failed", input));
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

    /// <summary>Single click on a protocol badge: web badges open in the browser, the ssh badge
    /// opens an interactive terminal session.</summary>
    private void ProtocolBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || (sender as FrameworkElement)?.DataContext is not ProtocolVm proto) return;
        if (proto.IsSsh)
        {
            LaunchSshTerminal(proto.Url["ssh://".Length..].Trim('[', ']'), RowDeviceFromVisual(sender));
            e.Handled = true;
        }
        else if (proto.IsFtp)
        {
            // File Explorer still browses ftp:// (browsers dropped it); go straight to explorer.exe.
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", proto.Url) { UseShellExecute = true }); }
            catch { /* explorer missing / blocked */ }
            e.Handled = true;
        }
        else if (proto.IsTelnet)
        {
            LaunchTelnet(proto.Url["telnet://".Length..].Trim('[', ']'));
            e.Handled = true;
        }
        else if (proto.IsRdp)
        {
            LaunchRdp(proto.Url["rdp://".Length..]);
            e.Handled = true;
        }
        else if (proto.IsVnc)
        {
            OpenVnc(proto.Url["vnc://".Length..]);
            e.Handled = true;
        }
        else if (proto.IsSmb)
        {
            // Expand the row so the SMB share buttons become visible.
            var d = sender as DependencyObject;
            while (d is not null and not DataGridRow) d = VisualTreeHelper.GetParent(d);
            if (d is DataGridRow row)
            {
                if (row.DataContext is DeviceViewModel dv) dv.IsExpanded = true;
                else if (row.DataContext is Ipv6RowVm rv) rv.IsExpanded = true;
                row.DetailsVisibility = Visibility.Visible;
            }
            e.Handled = true;
        }
        else if (proto.IsRtsp)
        {
            // A configured VLC gets the stream directly; otherwise whatever owns rtsp:// system-wide.
            // No player either way → the status bar says what is missing instead of failing silently.
            try
            {
                var vlc = _appData.VlcPath.Trim();
                if (vlc.Length > 0 && System.IO.File.Exists(vlc))
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(vlc, proto.Url) { UseShellExecute = true });
                else
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(proto.Url) { UseShellExecute = true });
            }
            catch { SetStatus(T("Rtsp_NoPlayer")); }
            e.Handled = true;
        }
        else if (proto.IsWeb)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(proto.Url) { UseShellExecute = true }); }
            catch { /* no browser / blocked */ }
            e.Handled = true;
        }
    }

    /// <summary>Opens an interactive SSH session to the host: with the external client from the
    /// settings when configured (e.g. PuTTY), otherwise with the built-in OpenSSH client – in
    /// Windows Terminal when available, else in a classic console window.</summary>
    private void LaunchSshTerminal(string host, DeviceViewModel? vm)
    {
        var user = vm?.Model.Username.Trim() ?? "";
        var target = user.Length > 0 ? $"{user}@{host}" : host;
        var sshPort = vm?.Model.SshPort ?? 22;

        var external = _appData.ExternalSshClientPath.Trim();
        if (_appData.UseExternalSshClient && external.Length > 0)
        {
            // PuTTY/KiTTY use -P for the port; everything else gets OpenSSH-style arguments.
            var exeName = Path.GetFileNameWithoutExtension(external);
            bool putty = exeName.Contains("putty", StringComparison.OrdinalIgnoreCase) ||
                         exeName.Contains("kitty", StringComparison.OrdinalIgnoreCase);
            var extArgs = putty
                ? (sshPort != 22 ? $"-ssh {target} -P {sshPort}" : $"-ssh {target}")
                : (sshPort != 22 ? $"-p {sshPort} {target}" : target);
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(external, extArgs) { UseShellExecute = true });
            }
            catch { SetStatus(T("Ssh_LaunchFailed")); }
            return;
        }

        // Offer only the plain (non-ETM) HMACs: some embedded servers (Zyxel firewalls) miscompute the
        // encrypt-then-MAC variants and drop every packet with "Corrupted MAC on input". Every device
        // we reach accepts these, so this is safe for MikroTik/APs too.
        var macs = $"-o MACs={TikMan.Core.Api.SshCompat.OpenSshMacList}";
        var sshArgs = sshPort != 22 ? $"{macs} -p {sshPort} {target}" : $"{macs} {target}";
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("wt.exe", $"ssh {sshArgs}") { UseShellExecute = true });
        }
        catch (Exception)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k ssh {sshArgs}") { UseShellExecute = true });
            }
            catch { SetStatus(T("Ssh_LaunchFailed")); }
        }
    }

    /// <summary>Opens a telnet session in a terminal. Telnet is an optional Windows feature that is
    /// off by default, so a missing client is reported with a hint instead of a silent failure.</summary>
    private void LaunchTelnet(string host)
    {
        var telnet = Path.Combine(Environment.SystemDirectory, "telnet.exe");
        if (!File.Exists(telnet))
        {
            MessageBox.Show(this, T("Telnet_NotInstalled"), "TikMan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("wt.exe", $"telnet {host}") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k telnet {host}") { UseShellExecute = true });
            }
            catch { SetStatus(T("Telnet_NotInstalled")); }
        }
    }

    /// <summary>Opens a Remote Desktop session to host:port with the built-in Windows client (mstsc).</summary>
    private void LaunchRdp(string endpoint)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("mstsc.exe", $"/v:{endpoint}") { UseShellExecute = true });
        }
        catch { SetStatus(T("Rdp_Failed")); }
    }

    /// <summary>Opens host:port in the built-in VNC viewer, after a one-off-per-open advisory that a
    /// dedicated standalone client is more secure/capable (the notice can be turned off in settings).</summary>
    private void OpenVnc(string endpoint)
    {
        var (host, port) = SplitEndpoint(endpoint, 5900);
        if (_appData.ShowVncNotice)
        {
            // Yes/No with "No" as the default, so an accidental Enter cancels.
            var answer = MessageBox.Show(this, T("Vnc_NoticeText"), T("Vnc_NoticeTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        try { new VncViewerWindow(host, port) { Owner = this }.Show(); }
        catch (Exception ex) { SetStatus($"{T("Vnc_Failed")} {ex.Message}"); }
    }

    /// <summary>Splits "host:port" or "[ipv6]:port" into its parts (falls back to defaultPort).</summary>
    private static (string Host, int Port) SplitEndpoint(string endpoint, int defaultPort)
    {
        if (endpoint.StartsWith('['))
        {
            int close = endpoint.IndexOf(']');
            var host = close > 0 ? endpoint[1..close] : endpoint.Trim('[', ']');
            var rest = close > 0 ? endpoint[(close + 1)..] : "";
            return (host, rest.StartsWith(':') && int.TryParse(rest[1..], out var p) ? p : defaultPort);
        }
        int colon = endpoint.LastIndexOf(':');
        return colon > 0 && int.TryParse(endpoint[(colon + 1)..], out var pp)
            ? (endpoint[..colon], pp) : (endpoint, defaultPort);
    }

    /// <summary>Finds the device VM of the row a badge/button lives in (works for both views).</summary>
    private static DeviceViewModel? RowDeviceFromVisual(object sender)
    {
        var d = sender as DependencyObject;
        while (d is not null and not DataGridRow)
            d = VisualTreeHelper.GetParent(d);
        return RowDevice((d as DataGridRow)?.DataContext);
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

    /// <summary>Copies text to the clipboard and confirms with a toast (plus the status bar).</summary>
    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(T("Msg_Copied"));
            ShowCopyToast(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard is briefly locked by another app – ignore
        }
    }

    /// <summary>Bottom-centre toast confirming what was copied; holds a moment, then fades out.</summary>
    private void ShowCopyToast(string text)
    {
        CopyToastText.Text = T("Toast_Copied", text.Length > 80 ? text[..80] + "…" : text);
        CopyToast.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromMilliseconds(1600),
        };
        fade.Completed += (_, _) => CopyToast.Visibility = Visibility.Collapsed;
        CopyToast.BeginAnimation(OpacityProperty, fade); // replaces a still-running fade
    }

    /// <summary>Copies the full IEEE vendor record (name + address) for the row's MAC.</summary>
    private void CopyVendorFull_Click(object sender, RoutedEventArgs e)
    {
        if (RowDevice((sender as MenuItem)?.DataContext) is not { } vm) return;
        var entry = OuiLookup.GetFullEntry(vm.Model.MacAddress);
        if (entry.Length > 0) CopyToClipboard(entry);
    }

    /// <summary>Shows the full IEEE OUI record (name + address) for the row's MAC in a copyable popup.</summary>
    private void VendorInfo_Click(object sender, RoutedEventArgs e)
    {
        if (RowDevice((sender as FrameworkElement)?.DataContext) is not { } vm) return;
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

    /// <summary>Debounced so typing stays responsive: filtering runs shortly after the user pauses,
    /// with a spinner shown meanwhile, instead of re-filtering on every keystroke.</summary>
    private void DeviceFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterSpinner.Visibility = Visibility.Visible;
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void FilterDebounce_Tick(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        ApplyDeviceFilter();
        FilterSpinner.Visibility = Visibility.Collapsed;
    }

    /// <summary>Escape clears the filter box.</summary>
    private void DeviceFilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DeviceFilterBox.Clear(); e.Handled = true; }
    }

    private void ApplyDeviceFilter()
    {
        var view = CollectionViewSource.GetDefaultView(DeviceGrid.ItemsSource);
        var tokens = DeviceFilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        view.Filter = _v6Mode
            ? obj => obj is Ipv6RowVm r && (tokens.Length == 0 || DeviceMatchesFilter(r.Device, tokens))
            // The v4 view only lists devices that actually have an IPv4 (v6-only ones live in the IPv6 tab).
            : obj => obj is DeviceViewModel d && d.HasIpv4 && (tokens.Length == 0 || DeviceMatchesFilter(d, tokens));
        UpdateDeviceCount(); // the "shown" count reflects the filter just applied
    }

    /// <summary>Every term must match somewhere on the device (AND) – each extra term narrows the
    /// result. The protocol badges count as searchable text, so "snmp" finds everything with an snmp
    /// badge and "snmp ssh" only what carries both.</summary>
    private static bool DeviceMatchesFilter(DeviceViewModel d, string[] tokens)
    {
        var haystack = string.Join(" ",
            d.Name, d.AllAddressesText, d.Model.MacAddress, d.TransportDisplay,
            string.Join(" ", d.SupportedProtocols.Select(p => p.Name)),   // the badges: ssh, snmp, airprint …
            d.Hypervisor,
            d.MacVendor, d.IdentifiedVendor, d.DeviceType, d.ModelDisplay, d.Version, d.LatestWithChannel,
            d.CpuText, d.MemoryText, d.Uptime, d.UpdateChannel, d.StatusText, d.LastError);
        return tokens.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailTabs.DataContext = SelectedDevice; // the bottom tabs always talk to the device itself
        UpdateDeviceCount();                     // reflect the new selection count
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

    // The single-device .rsc / .backup buttons lived here. The backup wizard (BackupAllWindow) does
    // the same job better – it picks the devices that actually have a login, sets their order and
    // fetches the binary image alongside the config. DeviceViewModel.DownloadConfigAsync and
    // DownloadFullBackupAsync stay: the wizard and the web server both use them.

    // ----- Selection / progress -----

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

    /// <summary>What used to bracket the update dialog's ShowDialog(): pause the poll timer while
    /// updates run (a monitoring query mid-reboot is noise at best), and write any reordering back when
    /// they're done. In a tab there is no closing moment to hang that off, so the view says when.</summary>
    private void UpdatesView_RunningChanged(object? sender, bool running)
    {
        if (running)
        {
            _updatesWasPolling = _pollTimer.IsEnabled;
            _pollTimer.IsEnabled = false;
        }
        else
        {
            _pollTimer.IsEnabled = _updatesWasPolling;
            ApplyUpdateOrder(UpdatesView.OrderedDevices);
        }
    }

    private bool _updatesWasPolling;

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

    /// <summary>Opens a pre-filled feature-request e-mail (no log attached).</summary>
    private void RequestFeature_Click(object sender, RoutedEventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var version = v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        var subject = T("Feat_Subject", version);
        var body = T("Feat_Body");

        if ((!_appData.ForceMailFallback && ProblemReporter.TryOutlookClassic(subject, body, "")) ||
            ProblemReporter.TryMailto(subject, body))
            SetStatus(T("Feat_Opened"));
        else
            SetStatus(T("Feat_Failed"));
    }

    /// <summary>Coffee button: opens the Ko-fi donation page in the default browser.</summary>
    private void BuyCoffee_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ko-fi.com/pascalmontico") { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(this, "https://ko-fi.com/pascalmontico", T("Coffee_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Switches the main list between the IPv4 view (one row per device with an IPv4) and
    /// the IPv6 view (one row per IPv6 address, device groups in alternating colours).</summary>
    private void AddressTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, AddressTabs) || Ipv6Column is null || !IsLoaded) return;

        // Tabs 3/4 swap the grid for a map (IP distribution / topology), 5/6 for an assistant.
        int tab = AddressTabs.SelectedIndex;
        BackupHost.Visibility = tab == 4 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesHost.Visibility = tab == 5 ? Visibility.Visible : Visibility.Collapsed;
        // Fill the assistants on the way in – devices and their versions change with every scan, and
        // both views keep what the user set for the devices that are still here.
        if (tab == 4) BackupView.Load(_devices.Where(d => d.HasCredentials).ToList(), _appData.BackupMethod);
        if (tab == 5) UpdatesView.Load(MarkedDevices() is { Count: > 0 } marked ? marked : _devices.ToList());
        if (tab is 2 or 3)
        {
            ShowTopology(physical: tab == 3);
            return;
        }
        HideTopology();
        if (tab >= 4)
        {
            DeviceGrid.Visibility = Visibility.Collapsed;
            return;
        }

        _v6Mode = AddressTabs.SelectedIndex == 1;
        if (_v6Mode)
        {
            BuildV6Rows();
            DeviceGrid.ItemsSource = _v6Rows;
            Ipv6Column.DisplayIndex = _addressColumnIndex; // IPv6 before IPv4
            Ipv6GroupColumn.Visibility = Visibility.Visible; // group number only makes sense per address
            var view = CollectionViewSource.GetDefaultView(_v6Rows);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(Ipv6RowVm.GroupSortKey), ListSortDirection.Ascending));
        }
        else
        {
            DeviceGrid.ItemsSource = _devices;
            Ipv4Column.DisplayIndex = _addressColumnIndex; // IPv4 before IPv6 again
            Ipv6GroupColumn.Visibility = Visibility.Collapsed;
            var view = CollectionViewSource.GetDefaultView(_devices);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(DeviceViewModel.Ipv4SortKey), ListSortDirection.Ascending));
        }
        ApplyDeviceFilter();
    }

    /// <summary>(Re)builds the IPv6 view's rows: one row per IPv6 address, only devices that have
    /// IPv6, grouped per device with alternating white/ice-blue backgrounds.</summary>
    private void BuildV6Rows()
    {
        var expanded = _v6Rows.Where(r => r.IsExpanded).Select(r => r.Device).ToHashSet();
        _v6Rows.Clear();
        int group = 0;
        foreach (var vm in _devices.Where(d => d.HasIpv6)
                                   .OrderBy(d => d.Ipv6List[0], StringComparer.OrdinalIgnoreCase))
        {
            group++;
            var bg = group % 2 == 1 ? Brushes.Transparent : Ipv6GroupBrush;
            bool first = true;
            foreach (var addr in vm.Ipv6List)
            {
                var row = new Ipv6RowVm(vm, addr, first, bg, group);
                if (row.HasRowDetails && (expanded.Contains(vm) || _appData.ExpandRowsByDefault))
                    row.IsExpanded = true;
                _v6Rows.Add(row);
                first = false;
            }
        }
    }

    /// <summary>Keeps the IPv6 view current while discovery merges addresses or finds SMB.</summary>
    private void Device_PropertyChangedForV6(object? sender, PropertyChangedEventArgs e)
    {
        if (_v6Mode && e.PropertyName is nameof(DeviceViewModel.Ipv6Display) or nameof(DeviceViewModel.HasSmb))
            BuildV6Rows();
    }

    /// <summary>Expands a row right away when the "rows expanded by default" option is on and the
    /// device has something to show. Called when devices appear or gain details.</summary>
    private void ApplyDefaultExpansion(DeviceViewModel vm)
    {
        if (_appData.ExpandRowsByDefault && vm.HasRowDetails && !vm.IsExpanded) vm.IsExpanded = true;
    }

    /// <summary>Shows/hides the row details. Set as a local value because with
    /// RowDetailsVisibilityMode=Collapsed the DataGrid coerces style-trigger values back to Collapsed.</summary>
    private void Expander_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        while (d is not null and not DataGridRow)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is DataGridRow row)
            row.DetailsVisibility = ((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Keeps recycled row containers in sync with the item's expanded state.</summary>
    private void DeviceGrid_LoadingRow(object sender, DataGridRowEventArgs e) =>
        e.Row.DetailsVisibility = e.Row.DataContext switch
        {
            DeviceViewModel { IsExpanded: true } or Ipv6RowVm { IsExpanded: true } => Visibility.Visible,
            _ => Visibility.Collapsed,
        };

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
