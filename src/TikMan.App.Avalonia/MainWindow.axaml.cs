using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using TikMan.Core.Api;
using TikMan.Core.Diagnostics;
using TikMan.Core.Discovery;
using TikMan.Core.Fleet;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm = new();

    // The last-built layout per view – the export renders from this data (transform-independent), not from
    // the on-screen (possibly zoomed/panned) canvas.
    private TopoLayout? _logicalLayout, _physicalLayout;

    // ⚠️ Resolved with FindControl, not x:Name fields: this view hosts custom controls (BackupAllView /
    // UpdateAllView), and the Avalonia generator then doesn't emit the typed fields – they stay null and the
    // ctor throws. Exactly the trap VncWindow and the assistants already hit; MainWindow joined them the
    // moment it embedded its own controls.
    private BackupAllView _backupView = null!;
    private UpdateAllView _updateView = null!;
    private TextBlock _publicIpV4Text = null!;
    private TextBlock _publicIpV6Text = null!;
    private TextBlock _publicIpSep = null!;
    private MenuItem _webToggleItem = null!;
    private MenuItem _webStatusItem = null!;
    private TabControl _tabs = null!;
    private Canvas _logicalCanvas = null!;
    private Canvas _physicalCanvas = null!;
    private TextBlock? _smartConnectHint;
    private MenuItem? _themeAutoItem, _themeLightItem, _themeDarkItem;
    private bool _topoBuilding;
    private TabControl? _detailTabs;
    private TabItem? _logTab;
    private DataGrid? _logGrid;
    private DataGrid _deviceGrid = null!;
    private Border? _detailPane;

    // The window's NORMAL-state placement, tracked live so we persist the restore bounds (not the maximised
    // size) on close. int.MinValue / 0 = not yet known.
    private double _normW, _normH;
    private int _normX = int.MinValue, _normY = int.MinValue;

    /// <summary>The scheduled update check. Created once the window is open, stopped when it closes.</summary>
    private AutoCheck? _autoCheck;
    private HistoryChart? _monitorChart;
    private ComboBox? _monitorInterval;
    private CheckBox? _logAutoRefresh;
    private ComboBox? _logCount;
    private ComboBox? _logInterval;
    private global::Avalonia.Threading.DispatcherTimer? _logTimer;
    // The intervals the dropdown offers, in seconds. Fixed set: a free-text box for a poll interval invites
    // a "0" or a "3600" that either hammers the switch or defeats the point.
    private static readonly int[] LogIntervalSeconds = { 1, 5, 15, 30, 60 };
    // The monitoring tab's CPU/RAM/uptime refresh choices, in seconds.
    private static readonly int[] MonitorIntervalSeconds = { 5, 10, 15, 30, 60, 120 };
    // See SyncDetailTab: the detail tab the user last chose, kept across the refresh that would otherwise
    // snap it shut, and a guard so our own restore isn't mistaken for a user choice.
    private int _preferredDetailTab;
    private bool _restoringDetailTab;
    // True while the VM is rebuilding the device list (every monitor refresh replaces the selected row,
    // because Cpu/RAM are in the row signature). During that churn the detail pane transiently snaps to
    // Details; we must not record that as the user's choice, and we restore the real tab once it settles.
    private bool _refreshingList;
    private TextBox? _logFilter;

    /// <summary>The last fetched log, unfiltered – so typing in the filter box re-filters locally instead of
    /// hitting the device again on every keystroke.</summary>
    private IReadOnlyList<TikMan.Core.Models.LogEntry> _logEntries = Array.Empty<TikMan.Core.Models.LogEntry>();
    /// <summary>Which device the log grid currently belongs to – so a selection change to a DIFFERENT device
    /// clears the panel (and reloads), while a same-device refresh/reselection leaves the reading alone.</summary>
    private string _logDeviceId = "";

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _backupView = this.FindControl<BackupAllView>("BackupView")!;
        _updateView = this.FindControl<UpdateAllView>("UpdateView")!;
        _publicIpV4Text = this.FindControl<TextBlock>("PublicIpV4Text")!;
        _publicIpV6Text = this.FindControl<TextBlock>("PublicIpV6Text")!;
        _publicIpSep = this.FindControl<TextBlock>("PublicIpSep")!;
        _webToggleItem = this.FindControl<MenuItem>("WebToggleItem")!;
        _webStatusItem = this.FindControl<MenuItem>("WebStatusItem")!;
        _tabs = this.FindControl<TabControl>("Tabs")!;
        _logicalCanvas = this.FindControl<Canvas>("LogicalCanvas")!;
        _physicalCanvas = this.FindControl<Canvas>("PhysicalCanvas")!;
        _smartConnectHint = this.FindControl<TextBlock>("SmartConnectHint");
        _themeAutoItem = this.FindControl<MenuItem>("ThemeAutoItem");
        _themeLightItem = this.FindControl<MenuItem>("ThemeLightItem");
        _themeDarkItem = this.FindControl<MenuItem>("ThemeDarkItem");
        _deviceGrid = this.FindControl<DataGrid>("DeviceGrid")!;
        WireRowDetails(_deviceGrid);
        WireFocusKeeper();
        WireDragSelect(_deviceGrid);
        WireColumnResize(_deviceGrid);
        WireScrollbarDrag();
        if (this.FindControl<DataGrid>("Ipv6Grid") is { } v6Grid) { WireColumnResize(v6Grid); WireRowDetails(v6Grid); }
        _detailTabs = this.FindControl<TabControl>("DetailTabs");
        // The Monitoring/Logs tabs hide for devices that don't support them. If the hidden tab was the
        // selected one, the pane would show its (blank) content with no visible header – snap back to
        // Details, which is always there.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "HasMonitoring" or "CanReadLogs" or nameof(_vm.SelectedDevice))
                SyncDetailTab();
        };
        // The list rebuild (every monitor refresh) replaces the selected row and churns the pane through
        // Details. Suppress tab-preference recording for the duration, then restore the user's tab once the
        // churn (and the by-id reselection) has fully settled – queued at Background priority so it runs
        // after all the transient selection/visibility changes, not in the middle of them.
        _vm.ListRefreshing += () => _refreshingList = true;
        _vm.ListRefreshed += () =>
        {
            _refreshingList = false;
            Dispatcher.UIThread.Post(SyncDetailTab, DispatcherPriority.Background);
        };
        _logTab = this.FindControl<TabItem>("LogTab");
        _logGrid = this.FindControl<DataGrid>("LogGrid");
        _detailPane = this.FindControl<Border>("DetailPane");
        ApplySimpleMode();
        // Restore the height the user dragged it to last time.
        if (_detailPane is not null) _detailPane.Height = _vm.DetailPaneHeight;
        // Keep it in bounds as the window resizes (and clamp the restored value once the window has a real
        // size), so a pane sized large on a big screen can't starve the list when the window shrinks.
        SizeChanged += (_, _) => ClampDetailPaneToBounds();
        // ⚠️ Re-clamp to the screen whenever the window moves to another display (dragged across monitors,
        // or an RDP/streamed session whose resolution changed on reconnect) OR its state changes. The
        // Opened-time clamp alone missed the 720p beamer and the iOS RDP client, where the usable height only
        // becomes small AFTER the first layout, or changes mid-session.
        PositionChanged += (_, _) => ClampToScreen();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) ClampToScreen(); };

        // Restore the placement the user left the window at (position, normal size, maximised-or-not), so it
        // opens the same next time. Done in the ctor so it takes before the first show; ClampToScreen still
        // pulls it back on-screen if the saved size no longer fits. A non-positive saved size = never saved.
        var placement = _vm.Settings;
        if (placement.WindowWidth > 200 && placement.WindowHeight > 200)
        {
            Width = _normW = placement.WindowWidth;
            Height = _normH = placement.WindowHeight;
        }
        if (placement.WindowX != int.MinValue && placement.WindowY != int.MinValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(placement.WindowX, placement.WindowY);
            _normX = placement.WindowX; _normY = placement.WindowY;
        }
        if (placement.WindowMaximized) WindowState = WindowState.Maximized;
        // Track the NORMAL bounds live: while maximised, Width/Height/Position report the maximised values, so
        // capturing them only in the Normal state keeps the size we should restore to.
        SizeChanged += (_, _) => { if (WindowState == WindowState.Normal) { _normW = Width; _normH = Height; } };
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) { _normX = Position.X; _normY = Position.Y; } };
        _monitorChart = this.FindControl<HistoryChart>("MonitorChart");
        _logAutoRefresh = this.FindControl<CheckBox>("LogAutoRefresh");
        _logCount = this.FindControl<ComboBox>("LogCount");
        _logFilter = this.FindControl<TextBox>("LogFilter");

        _logInterval = this.FindControl<ComboBox>("LogInterval");
        if (_logCount is not null)
        {
            _logCount.ItemsSource = new[] { "100", "500", T("Av_LogAll") };
            // Default from the setting (100 out of the box): fetches fast, and enough for a quick look.
            _logCount.SelectedIndex = _vm.LogRowCap switch { 500 => 1, <= 0 => 2, _ => 0 };
        }
        if (_logInterval is not null)
        {
            _logInterval.ItemsSource = LogIntervalSeconds.Select(s => $"{s}s").ToList();
            var idx = Array.IndexOf(LogIntervalSeconds, _vm.LogRefreshSeconds);
            _logInterval.SelectedIndex = idx >= 0 ? idx : Array.IndexOf(LogIntervalSeconds, 5);
        }

        _monitorInterval = this.FindControl<ComboBox>("MonitorInterval");
        if (_monitorInterval is not null)
        {
            _monitorInterval.ItemsSource = MonitorIntervalSeconds.Select(s => $"{s}s").ToList();
            var idx = Array.IndexOf(MonitorIntervalSeconds, _vm.MonitorIntervalSeconds);
            _monitorInterval.SelectedIndex = idx >= 0 ? idx : Array.IndexOf(MonitorIntervalSeconds, 30);
        }

        // The auto-refresh timer. Interval and enabled-state are driven by UpdateLogAutoRefresh; the tick
        // just refetches (skipping a tick that would overlap a fetch still running).
        _logTimer = new DispatcherTimer();
        _logTimer.Tick += async (_, _) =>
        {
            if (_vm.LogLoading) return;
            if (_logTab is null || !ReferenceEquals(_detailTabs?.SelectedItem, _logTab)) return;
            await ReloadLogsAsync();
        };

        // Only shown when the capture driver is genuinely missing – a permanent warning would be noise.
        if (this.FindControl<TextBlock>("CaptureWarning") is { } warn)
            warn.IsVisible = !ZdpScanner.IsAvailable();

        MarkTheme(_vm.Settings.Theme);

        Title = BuildTitle();
        DataContext = _vm;
        _backupView.Attach(_vm.Fleet);
        _updateView.Attach(_vm.Fleet, _vm.Settings);
        // When every credential-based re-read has finished, the topology's evidence is complete – rebuild it
        // automatically so a saved login yields the fresh map with no manual "rearrange". Marshalled to the
        // UI thread (the event fires on the last read's worker thread).
        _vm.Fleet.RescanCompleted += () => Dispatcher.UIThread.Post(OnRescanCompleted);
        // The chart is not bindable to a method result, and the log has no reason to refetch on every
        // fleet tick unless the user asked for it – so both are driven from the view model's notifications.
        _vm.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName == nameof(_vm.SimpleScanMode)) { ApplySimpleMode(); return; }
            // The auto-refresh checkbox is bound to the VM, so its toggle arrives here – restart the timer.
            if (args.PropertyName == nameof(_vm.LogAutoRefresh)) { UpdateLogAutoRefresh(); return; }
            if (args.PropertyName is not (nameof(_vm.SelectedDevice) or nameof(_vm.Devices))) return;
            if (_monitorChart is not null) _monitorChart.Snapshots = _vm.SelectedHistory;
            // ⚠️ Auto-refresh runs on its OWN timer now (UpdateLogAutoRefresh), not off every fleet tick.
            // The old way fired at the fleet's cadence – roughly 30 s, and not adjustable – which is exactly
            // what the interval dropdown exists to control. Switching device does still reset the timer so a
            // stale log for the previous device isn't left ticking.
            UpdateLogAutoRefresh();

            // ⚠️ The log panel is imperative (unlike the Details/Monitoring tabs, which bind to the VM and
            // update themselves). So a device change must refresh it by hand – otherwise the panel keeps the
            // PREVIOUS device's log, showing one device's data while another is selected. Only on a REAL device
            // change (different id), so a 30-second list refresh / by-id reselection doesn't wipe what's being
            // read. Clear immediately; reload only when the Logs tab is the one on show and the new device can
            // actually read logs (else leave it empty – never another device's entries).
            if (args.PropertyName == nameof(_vm.SelectedDevice))
            {
                var id = _vm.SelectedDevice?.Id ?? "";
                if (id != _logDeviceId)
                {
                    _logDeviceId = id;
                    _logEntries = Array.Empty<TikMan.Core.Models.LogEntry>();
                    ApplyLogFilter();
                    if (_logTab is not null && ReferenceEquals(_detailTabs?.SelectedItem, _logTab) && _vm.CanReadLogs)
                        await ReloadLogsAsync();
                }
            }
        };

        Opened += async (_, _) =>
        {
            ClampToScreen();
            ApplyDefaultDetailPaneHeight();

            // ⚠️ After Load, not in the ctor: the columns exist by then, so DisplayIndex and Width take.
            GridLayout.Restore(_deviceGrid, _vm.Settings);
            GridLayout.Track(_deviceGrid);
            if (this.FindControl<DataGrid>("Ipv6Grid") is { } v6)
            {
                GridLayout.Restore(v6, _vm.Settings, GridLayout.Slot.Ipv6);
                GridLayout.Track(v6);
            }

            await _vm.StartupAsync();
            RefreshWebMenu();
            await _vm.LoadPublicIpAsync();
            _publicIpV4Text.Text = _vm.PublicV4Display;
            _publicIpSep.Text = _vm.PublicV4Display.Length > 0 ? "  ·  " : "";
            _publicIpV6Text.Text = _vm.PublicV6Display;

            // Arm the nightly check once the window is up. A missed slot (app closed at 03:00) is caught up
            // on this first tick, which is the point of the schedule on a desktop app.
            _autoCheck = new AutoCheck(_vm.Fleet, _vm.Settings, _vm.ReportAction, _vm.SaveSettings);

            ApplyMinimumWidth();
        };
        Closed += (_, _) =>
        {
            _autoCheck?.Stop();
            GridLayout.Capture(_deviceGrid, _vm.Settings);
            if (this.FindControl<DataGrid>("Ipv6Grid") is { } v6c)
                GridLayout.Capture(v6c, _vm.Settings, GridLayout.Slot.Ipv6);
            // Persist the window placement for next time: the maximised flag plus the NORMAL bounds (so
            // un-maximising restores the right size). Sizes ≤ 200 mean we never got a real normal layout.
            var s = _vm.Settings;
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (_normW > 200 && _normH > 200) { s.WindowWidth = _normW; s.WindowHeight = _normH; }
            if (_normX != int.MinValue && _normY != int.MinValue) { s.WindowX = _normX; s.WindowY = _normY; }
            _vm.SaveSettings();
            // The dashboard mirrors this window, so it has no business outliving it – and its port would
            // stay bound until the process exited.
            _vm.ShutDown();

        };
    }

    /// <summary>"TikMan 2.2.2 · 2026-07-18" – version plus the date this binary was built. The date rides in
    /// the informational version as "+build.yyyy-MM-dd" (stamped by the csproj); if it isn't there, the title
    /// simply drops it rather than showing a made-up date.</summary>
    /// <summary>Caps MinHeight/MinWidth to the CURRENT screen's usable area, and pulls the window itself back
    /// on-screen if it already overshoots. The XAML MinHeight (830) is taller than a small display's work
    /// area – a 720p beamer (720 px), or an iOS RDP client streaming a resolution below 830 – so a maximised
    /// window gets FORCED taller than the screen and its bottom strip (horizontal scrollbar + status bar)
    /// hangs invisibly below the edge. Runs at Opened and on every screen/state change, since the streamed
    /// resolution can drop only after the first layout or change mid-session.</summary>
    private void ClampToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var usableH = screen.WorkingArea.Height / scale - 8;
        var usableW = screen.WorkingArea.Width / scale - 8;
        if (usableH > 200 && MinHeight > usableH) MinHeight = usableH;
        if (usableW > 300 && MinWidth > usableW) MinWidth = usableW;
        // Not while maximised (the OS owns the size then, and it already fits the work area); only a normal
        // window that a shrunken screen left overshooting gets pulled back so its bottom edge is visible.
        if (WindowState == WindowState.Normal)
        {
            if (usableH > 200 && Height > usableH) Height = usableH;
            if (usableW > 300 && Width > usableW) Width = usableW;
        }
    }

    private static string BuildTitle()
    {
        var asm = typeof(MainWindow).Assembly;
        var version = TikMan.Core.AppVersion.Text(asm);

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var marker = info.IndexOf("+build.", StringComparison.Ordinal);
        var built = marker >= 0 ? info[(marker + "+build.".Length)..] : "";

        // A Debug build carries the auto-incremented dev counter after the date ("2026-08-11.17") – shown as
        // a fourth version part ("TikMan 2.3.4.17"), so during an iterate-test loop the title says exactly
        // which build is running. Releases have no counter and keep the clean three-part version.
        var counter = "";
        if (built.IndexOf('.') is > 0 and var dot) { counter = "." + built[(dot + 1)..]; built = built[..dot]; }

        var title = version.Length > 0 ? $"TikMan {version}{counter}" : "TikMan";
        // Label the build date so it reads as one ("Build 2026-07-30"), not a bare number after a dot – the
        // point is to see at a glance how old the running executable is.
        return built.Length > 0 ? $"{title}  ·  Build {built}" : title;
    }

    private void OnScanClick(object? sender, RoutedEventArgs e) => _vm.ToggleScan();
    private void OnRescanClick(object? sender, RoutedEventArgs e) => _vm.RescanMarked();
    private void OnWakeClick(object? sender, RoutedEventArgs e) => _vm.Wake();
    private void OnFirmwarePage(object? sender, RoutedEventArgs e) => _vm.OpenFirmwarePage();

    /// <summary>Wake by typed address. ⚠️ A sleeping machine is by definition absent from the device list,
    /// so this cannot work off the selection – the user has to be able to name it.</summary>
    private async void OnWakeMacClick(object? sender, RoutedEventArgs e)
    {
        var entered = await TextPromptWindow.AskAsync(this, T("Av_WakeDialogTitle"), T("Av_WakeDialogPrompt"));
        if (string.IsNullOrWhiteSpace(entered)) return;
        _vm.WakeTarget(entered.Trim());
    }

    private async void OnWebToggleClick(object? sender, RoutedEventArgs e)
    {
        // Starting without a login: offer to open the settings rather than silently doing nothing. The old
        // status line used to show this; with it gone, an ignored click was the only feedback.
        if (!_vm.WebRunning && _vm.WebCredentialsMissing)
        {
            if (await ConfirmWindow.AskAsync(this, T("Av_WebNeedCreds"), T("Av_OpenSettings")))
                await OpenSettingsAsync();
            RefreshWebMenu(); // the toggle tick reflects WebRunning, still false – put it back
            return;
        }
        _vm.ToggleWebServer();
        RefreshWebMenu();
    }

    private void OnWebOpenClick(object? sender, RoutedEventArgs e) => _vm.OpenWebServer();

    /// <summary>The URL in the running-server banner: opens the dashboard in the browser.</summary>
    private void OnWebBannerClick(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _vm.OpenWebServer();
    }

    /// <summary>Keeps the web-server menu honest: the toggle says start/stop – with a play triangle in
    /// front of "start" and a stop square in front of "stop" – and the (disabled) status line shows the
    /// bound URL while it runs. Built in code because the whole header flips with the state anyway.</summary>
    private void RefreshWebMenu()
    {
        var running = _vm.WebRunning;
        var icon = new global::Avalonia.Controls.Shapes.Path
        {
            Data = this.FindResource(running ? "IconStopSquare" : "IconPlay") as Geometry,
            Width = 11, Height = 11,
            Stretch = global::Avalonia.Media.Stretch.Uniform,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        // Bound, not assigned: GlyphBrush lives in the theme dictionaries, and a one-time lookup would
        // freeze the icon in whichever theme was active when the menu last refreshed.
        icon.Bind(global::Avalonia.Controls.Shapes.Shape.FillProperty, icon.GetResourceObservable("GlyphBrush"));
        _webToggleItem.Header = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = running ? T("Av_WebStop") : T("Web_Enable"),
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                },
            },
        };
        _webStatusItem.Header = running ? _vm.WebUrl : T("Web_Stopped");
    }

    private async void OnPublicV4Click(object? sender, PointerPressedEventArgs e)
    {
        if (_vm.PublicV4.Length == 0) return;
        await CopyToClipboardAsync(_vm.PublicV4);
    }

    private async void OnPublicV6Click(object? sender, PointerPressedEventArgs e)
    {
        if (_vm.PublicV6.Length == 0) return;
        await CopyToClipboardAsync(_vm.PublicV6);
    }

    private int _toastVersion;

    /// <summary>Brief confirmation floating over the window – for copies, where the action leaves no other
    /// trace. Deliberately immediate: shown before anything slow happens, hidden a second and a half later.
    /// <para>The version counter is what makes rapid copies behave: each toast only hides itself if it is
    /// still the current one, so an earlier timer cannot blank a newer message.</para></summary>
    private void ShowToast(string message)
    {
        var toast = this.FindControl<Border>("Toast");
        var label = this.FindControl<TextBlock>("ToastText");
        if (toast is null || label is null) return;

        label.Text = message;
        toast.IsVisible = true;
        var version = ++_toastVersion;
        global::Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (version == _toastVersion) toast.IsVisible = false;
        }, TimeSpan.FromMilliseconds(1500));
    }

    /// <summary>Puts text on the system clipboard and says so. The clipboard hangs off the TopLevel, so this
    /// has to live in the window rather than the view model.</summary>
    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        // ⚠️ Before the await, not after. The clipboard write is an async round trip through the OS and can
        // take a noticeable moment; showing the toast first is what makes the feedback feel instant.
        ShowToast(T("Av_Copied", text));
        try
        {
            // ⚠️ Avalonia 12 replaced IClipboard.SetTextAsync with the DataTransfer model – a clipboard
            // entry is now a DataTransfer holding one or more typed items.
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch
        {
            // Another app can hold the clipboard open. Say so rather than leaving a toast claiming success.
            ShowToast(T("Av_CopyFailed"));
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        await new SettingsWindow(_vm.Settings).ShowDialog(this);
        _vm.SettingsChanged(); // the dialog edits AppData directly – re-read the view-affecting settings
        _autoCheck?.Apply();   // arm or disarm the nightly check to match what was just saved
        RefreshWebMenu();      // web creds may now exist – reflect start/stop availability
    }

    /// <summary>A click on a service badge opens that service: web URLs in the browser, vnc in the built-in
    /// viewer, ssh in the built-in terminal, everything else handed to the OS handler.</summary>
    private void OnBadgeClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control badge) return;

        // The SMB/NetBIOS badge is a shortcut to the shares, not a launcher: clicking it opens the row's
        // detail area, where the share buttons live. Works on both grids – the IPv4 row is a DeviceSnapshot,
        // the IPv6 row an Ipv6Row, and both are IExpandableRow. Handled before the URL guard because these
        // badges carry no URL. (The grid selects the row on pointer-press, so we don't swallow the event.)
        if (badge.DataContext is ServiceBadge { Name: "smb" or "netbios" })
        {
            var rowItem = RowItemOf(badge);
            if (rowItem is not null) rowItem.IsExpanded = true;
            _vm.SelectedDevice = rowItem switch
            {
                DeviceSnapshot d => d,
                Ipv6Row r => r.Device,
                _ => _vm.SelectedDevice,
            };
            return;
        }

        if (badge.Tag is not string url || url.Length == 0) return;

        // ⚠️ The device must come from the row that was clicked, not from the current selection. Reading
        // _vm.SelectedDevice opened a VNC/SSH session to whatever was selected before – click the vnc badge
        // on row B while row A is selected and you land on A. Nor may this swallow the event: the DataGrid
        // selects the row on pointer-press, so handling it here would stop the click selecting its own row.
        var device = DeviceOf(badge);
        if (device is null) return;

        // Make the clicked row the selection before acting. The grid selects on pointer-press too, but the
        // order of the two handlers is not guaranteed – and OnTerminalClick works off the selection, so
        // without this the SSH badge could still open a terminal to the previously selected device.
        _vm.SelectedDevice = device;

        if (url.StartsWith("vnc://", StringComparison.OrdinalIgnoreCase) && device is { VncPort: > 0 })
        {
            new VncWindow(device.Ip, device.VncPort).Show(this);
            return;
        }
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            // The built-in terminal when the user asked for it (it prompts for a login when the device has
            // none) or when there is a stored login to use; otherwise hand off to a real SSH client so the
            // user can authenticate however they like.
            if (_vm.Settings.PreferBuiltInSsh || device.HasLogin) OnTerminalClick(sender, new RoutedEventArgs());
            else _vm.LaunchSsh(device);
            return;
        }
        if (url.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase)) { _vm.LaunchRdp(device); return; }
        if (url.StartsWith("telnet://", StringComparison.OrdinalIgnoreCase)) { _vm.LaunchTelnet(device); return; }
        if (url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)) { _vm.LaunchFtp(url); return; }
        if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) { _vm.LaunchRtsp(url); return; }
        _vm.OpenPath(url); // http/https – whatever the desktop has registered
    }

    /// <summary>The device a cell belongs to: walk up from the clicked element until a DataContext turns out
    /// to be a device. Inside the badge template the DataContext is the badge itself, so the row's device has
    /// to be fetched from an ancestor rather than assumed to be the current selection.</summary>
    private static DeviceSnapshot? DeviceOf(Control? from)
    {
        for (var c = from; c is not null; c = c.Parent as Control)
            if (c.DataContext is DeviceSnapshot d) return d;
        return null;
    }

    /// <summary>The expandable row a cell belongs to – a <see cref="DeviceSnapshot"/> in the IPv4 grid, an
    /// <see cref="Ipv6Row"/> in the IPv6 one. Same upward walk as <see cref="DeviceOf"/>, but stopping at
    /// whichever row type this grid uses, so the SMB badge can open the details on both.</summary>
    private static IExpandableRow? RowItemOf(Control? from)
    {
        for (var c = from; c is not null; c = c.Parent as Control)
            if (c.DataContext is IExpandableRow r) return r;
        return null;
    }

    /// <summary>Opens the link behind a firmware cell (its Tag): the Latest cell's vendor download page (or
    /// "manual search"), or the Version cell's per-release changelog. Empty Tag ⇒ nothing to open.</summary>
    private void OnLatestClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string url } && url.Length > 0) _vm.OpenWeb(url);
    }

    /// <summary>Opens an SMB share from the row details in the platform's file manager.</summary>
    private void OnShareClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string unc || unc.Length == 0) return;
        _vm.OpenFolder(unc);
    }

    // Context-menu launchers. These work off the selection (the right-click selects the row first), unlike
    // the badge handlers which have to resolve the row they were clicked in.
    private void OnRdpClick(object? sender, RoutedEventArgs e)
    { if (_vm.SelectedDevice is { } d) _vm.LaunchRdp(d); }

    private void OnSftpClick(object? sender, RoutedEventArgs e)
    { if (_vm.SelectedDevice is { } d) _vm.LaunchSftp(d); }

    private void OnTelnetClick(object? sender, RoutedEventArgs e)
    { if (_vm.SelectedDevice is { } d) _vm.LaunchTelnet(d); }

    // ---- detail pane tabs -------------------------------------------------------------------------

    /// <summary>Loads the log the first time the Logs tab is opened. Doing it on tab change rather than on
    /// selection keeps an SSH round trip off every click in the device list.</summary>
    private async void OnDetailTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_detailTabs?.SelectedItem is not TabItem tab) return;
        // Remember a deliberate switch to a visible tab, so a later refresh can put it back (see
        // SyncDetailTab). ⚠️ Not while WE are the one moving it – recording the transient snap-to-Details
        // as the user's preference would defeat the whole restore.
        // ⚠️ And NOT the TabControl's own auto-snap to Details: when a refresh replaces the selected row, the
        // device blips to null, the Monitoring/Logs tab hides, and the control auto-selects the first visible
        // tab (Details, index 0). That is not a user choice – recording it wiped the tab the user was reading
        // (the reported "it jumps back to the first tab on every update"). Tell the two apart: a snap to index
        // 0 while the tab the user preferred is currently hidden is the auto-snap, so leave the preference be.
        if (!_restoringDetailTab && !_refreshingList && tab.IsVisible)
        {
            var items = _detailTabs.Items.OfType<TabItem>().ToList();
            var preferredStillVisible = _preferredDetailTab >= 0 && _preferredDetailTab < items.Count
                                        && items[_preferredDetailTab].IsVisible;
            if (_detailTabs.SelectedIndex != 0 || preferredStillVisible)
                _preferredDetailTab = _detailTabs.SelectedIndex;
        }
        // Start/stop the auto-refresh timer to match whichever tab is now showing – it only ticks while the
        // Logs tab is the visible one.
        UpdateLogAutoRefresh();
        // ⚠️ Only refetch on a real user switch to the Logs tab. When the refresh briefly bounced the tab and
        // we restored it, re-fetching here would open an SSH log read on every 30-second cycle.
        if (_restoringDetailTab) return;
        if (!ReferenceEquals(tab, _logTab)) return;
        await ReloadLogsAsync();
    }

    /// <summary>Shows the user's preferred detail tab when it is available, otherwise Details. The 30-second
    /// refresh replaces the selected grid row (its CPU reading changed the row signature), which the grid
    /// briefly reports as a deselect – flipping Monitoring/Logs to hidden and snapping the pane back to
    /// Details. Restoring by intent keeps the tab the user is reading in front of them across every refresh.</summary>
    private void SyncDetailTab()
    {
        if (_detailTabs is null) return;
        var items = _detailTabs.Items.OfType<TabItem>().ToList();
        var want = _preferredDetailTab >= 0 && _preferredDetailTab < items.Count && items[_preferredDetailTab].IsVisible
            ? _preferredDetailTab : 0;
        if (_detailTabs.SelectedIndex == want) return;
        _restoringDetailTab = true;
        try { _detailTabs.SelectedIndex = want; }
        finally { _restoringDetailTab = false; }
    }

    private void OnMonitorIntervalChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _monitorInterval is null) return;
        var i = _monitorInterval.SelectedIndex;
        if (i >= 0 && i < MonitorIntervalSeconds.Length) _vm.MonitorIntervalSeconds = MonitorIntervalSeconds[i];
    }

    private async void OnReloadLogs(object? sender, RoutedEventArgs e) => await ReloadLogsAsync();

    /// <summary>Fetches the log and applies the current row cap and filter. The unfiltered result is kept so
    /// retyping the filter doesn't cost another SSH round trip.</summary>
    private async Task ReloadLogsAsync()
    {
        if (_logGrid is null) return;
        _logDeviceId = _vm.SelectedDevice?.Id ?? "";
        _logEntries = await _vm.LoadLogsAsync(LogRowCap());
        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        if (_logGrid is null) return;
        var needle = _logFilter?.Text?.Trim() ?? "";
        _logGrid.ItemsSource = needle.Length == 0
            ? _logEntries
            : _logEntries.Where(l =>
                l.Message.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                l.Topics.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                l.Time.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>How many entries to pull. "All" is 0, which the fleet reads as "no cap".</summary>
    private int LogRowCap() => (_logCount?.SelectedItem as string) switch
    {
        "100" => 100,
        "500" => 500,
        _ => 0,
    };

    private void OnLogFilterChanged(object? sender, TextChangedEventArgs e) => ApplyLogFilter();

    private void OnLogFilterKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox box) return;
        box.Text = "";
        e.Handled = true;
    }

    private async void OnLogCountChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Persist the choice (100 / 500 / all) so it survives a restart.
        if (IsLoaded) _vm.LogRowCap = (_logCount?.SelectedItem as string) switch
        {
            "500" => 500,
            _ when ReferenceEquals(_logCount?.SelectedItem, null) => _vm.LogRowCap,
            _ => _logCount!.SelectedIndex == 2 ? 0 : 100,
        };
        // Only worth refetching once the view is up and the Logs tab is the one being looked at.
        if (!IsLoaded || _logTab is null || !ReferenceEquals(_detailTabs?.SelectedItem, _logTab)) return;
        await ReloadLogsAsync();
    }

    private void OnLogIntervalChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _logInterval is null) return;
        var i = _logInterval.SelectedIndex;
        if (i >= 0 && i < LogIntervalSeconds.Length) _vm.LogRefreshSeconds = LogIntervalSeconds[i];
        UpdateLogAutoRefresh();   // apply the new interval right away
    }

    /// <summary>Starts or stops the log auto-refresh timer to match the checkbox, the chosen interval and
    /// whether the Logs tab is the one on screen. ⚠️ Only ticks while that tab is actually being looked at –
    /// polling a switch over SSH for a tab nobody has open is pure load on the device for nothing.</summary>
    private void UpdateLogAutoRefresh()
    {
        if (_logTimer is null) return;
        var on = _logAutoRefresh?.IsChecked == true
                 && _logTab is not null && ReferenceEquals(_detailTabs?.SelectedItem, _logTab);
        if (on)
        {
            _logTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_vm.LogRefreshSeconds, 1, 60));
            _logTimer.Stop();     // restart so a changed interval takes effect from now
            _logTimer.Start();
        }
        else _logTimer.Stop();
    }

    /// <summary>Resizes the detail pane by dragging the splitter above it. The pane has an explicit Height
    /// (it is docked to the bottom), so the drag adjusts that rather than a grid row.</summary>
    /// <summary>Shows only the columns simple mode can actually fill.
    ///
    /// <para>Simple mode is a promise about network chatter: no MNDP/mDNS/SSDP/ZON and no per-device probes.
    /// Type, vendor, model, serial, OS, firmware, protocol badges and the update columns all come from
    /// exactly those, so in simple mode they are columns that can only ever be blank. Address, name, MAC and
    /// the OUI vendor survive – the sweep and the MAC itself supply them.</para>
    ///
    /// <para>⚠️ Only hides columns; it never touches stored data. Switching back brings whatever a previous
    /// full scan collected straight back into view. What it cannot do is invent data that was never
    /// gathered: after a simple-mode scan those columns stay empty until a full scan runs, and the status
    /// line says so rather than leaving the user to wonder.</para></summary>
    private void ApplySimpleMode()
    {
        var grid = this.FindControl<DataGrid>("DeviceGrid");
        if (grid is null) return;

        var simple = _vm.SimpleScanMode;
        foreach (var column in grid.Columns)
            if (column.Tag as string == "rich")
                column.IsVisible = !simple;
    }

    /// <summary>Lets the user drag a column wider than its automatic size allows.
    ///
    /// <para>⚠️ The columns carry a MaxWidth so that <b>auto-sizing</b> stops at a sensible width instead of
    /// letting one long value (a full IPv6 list, a verbose SNMP model string) push everything else off
    /// screen. But Avalonia applies MaxWidth to the resize gripper too, so that same cap silently refused
    /// to let the user widen a column past it. Lifting the caps the moment a header is touched keeps both:
    /// the automatic width stays modest, and a deliberate drag is unlimited.</para></summary>
    /// <summary>While a scrollbar thumb is being dragged, tag its ScrollBar with a "dragging" class so the
    /// whole-bar hover emphasis switches off for the duration (the hover styles carry <c>:not(.dragging)</c>).
    /// The thumb captures the pointer, so <c>:pointerover</c> otherwise keeps every part lifted throughout the
    /// drag, which reads as a flickery colour change. Cleared on drag completion. Bubbles, so one pair of
    /// handlers on the window covers both grids' scrollbars.</summary>
    private void WireScrollbarDrag()
    {
        AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent,
            (object? _, global::Avalonia.Input.VectorEventArgs e) => SetScrollbarDragging(e.Source, true),
            global::Avalonia.Interactivity.RoutingStrategies.Bubble);
        AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent,
            (object? _, global::Avalonia.Input.VectorEventArgs e) => SetScrollbarDragging(e.Source, false),
            global::Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private static void SetScrollbarDragging(object? source, bool on)
    {
        if (source is not Visual v) return;
        var bar = global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v)
            .OfType<global::Avalonia.Controls.Primitives.ScrollBar>().FirstOrDefault();
        if (bar is null) return;
        if (on) { if (!bar.Classes.Contains("dragging")) bar.Classes.Add("dragging"); }
        else bar.Classes.Remove("dragging");
    }

    private void WireColumnResize(DataGrid grid)
    {
        var lifted = false;
        grid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (lifted || e.Source is not Visual v) return;
            var onHeader = v is DataGridColumnHeader ||
                global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v)
                    .Any(a => a is DataGridColumnHeader);
            if (!onHeader) return;
            lifted = true;
            foreach (var c in grid.Columns) c.MaxWidth = double.PositiveInfinity;
        }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Click-drag over rows marks the range between the press and the pointer, like the WPF list.
    ///
    /// <para>⚠️ Implemented here rather than relied on from the DataGrid, and by <b>item id</b> rather than
    /// row references: the fleet rebuild (four times a second during a scan) replaces every row object
    /// mid-gesture, which is what quietly killed the built-in behaviour – the grid's own drag state pointed
    /// at rows that no longer existed. Recomputing the range from ids on every pointer move survives any
    /// number of rebuilds.</para></summary>
    private void WireDragSelect(DataGrid grid)
    {
        string? anchorId = null;

        grid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            anchorId = null;
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
            // Ctrl/Shift-clicks belong to the grid's own extended-selection handling.
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0) return;
            if (e.Source is not Visual v) return;
            // A press on an interactive element (the expander, a badge with a click action) is that
            // element's business – dragging from it must not start marking rows.
            foreach (var a in global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v))
            {
                if (a is global::Avalonia.Controls.Primitives.ToggleButton or Button) return;
                if (a is DataGridRow row) { anchorId = (row.DataContext as DeviceSnapshot)?.Id; return; }
            }
        }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        grid.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (anchorId is null) return;
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) { anchorId = null; return; }

            // ⚠️ The row under the pointer is found by POSITION, not from e.Source. The grid captures the
            // pointer on the pressed cell, and under capture every move event reports that cell as its
            // source no matter where the pointer actually is – resolving rows from the source made the
            // drag permanently see its own anchor and never extend. This was the second life of this bug.
            var pos = e.GetPosition(grid);
            DataGridRow? row = null;
            foreach (var r in global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(grid)
                         .OfType<DataGridRow>())
            {
                if (r.TranslatePoint(new Point(0, 0), grid) is not { } tl) continue;
                if (pos.Y >= tl.Y && pos.Y < tl.Y + r.Bounds.Height) { row = r; break; }
            }
            if (row?.DataContext is not DeviceSnapshot current) return;

            var items = _vm.Devices;
            int a1 = -1, b1 = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Id == anchorId) a1 = i;
                if (ReferenceEquals(items[i], current)) b1 = i;
            }
            if (a1 < 0 || b1 < 0) return;

            var (lo, hi) = a1 <= b1 ? (a1, b1) : (b1, a1);
            // Only touch the selection when the range actually changed – rewriting it on every pixel of
            // movement makes the grid flicker and floods SelectionChanged.
            if (grid.SelectedItems.Count == hi - lo + 1 &&
                grid.SelectedItems.Contains(items[lo]) && grid.SelectedItems.Contains(items[hi])) return;
            grid.SelectedItems.Clear();
            for (var i = lo; i <= hi; i++) grid.SelectedItems.Add(items[i]);
        }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        grid.AddHandler(PointerReleasedEvent, (_, _) => anchorId = null,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Keeps keyboard focus on the device grid across list rebuilds.
    ///
    /// <para>⚠️ Every fleet change rebuilds the observable collection – Clear() plus re-add – and during a
    /// scan that happens four times a second. The rebuild destroys every realized row, and when the focused
    /// row is destroyed Avalonia drops focus back to the window: arrow keys stop working, the focus ring is
    /// gone, and it feels like the app lost interest in you mid-scan. The rebuild itself is fine (selection
    /// and marks are already carried across by id) – only the focus needed carrying too.</para></summary>
    private void WireFocusKeeper()
    {
        var gridHadFocus = false;
        _vm.ListRefreshing += () =>
        {
            var focused = GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            gridHadFocus = focused is Visual v &&
                           global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v).Contains(_deviceGrid);
        };
        // Posted: right after the rebuild the new rows exist as items but are not realized yet, so focusing
        // the grid synchronously would land on nothing.
        _vm.ListRefreshed += () =>
        {
            if (!gridHadFocus) return;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => _deviceGrid.Focus());
        };

        // The rebuild also wipes the grid's multi-selection: the view model restores SelectedItem by id,
        // but the ADDITIONAL marked rows live only in the grid's SelectedItems, which Clear() emptied.
        // Put them back from the view model's marks (already re-resolved to the new snapshot instances).
        _vm.ListRefreshed += () =>
        {
            var marked = _vm.Marked;
            if (marked.Count <= 1) return;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // ⚠️ Only items the grid currently holds. This runs one dispatcher turn late, and by then
                // another refresh may have replaced the list – adding a stale snapshot instance throws
                // "The item is not contained in the ItemsSource" and takes the window down with it.
                foreach (var m in marked)
                    if (_vm.Devices.Contains(m) && !_deviceGrid.SelectedItems.Contains(m))
                        _deviceGrid.SelectedItems.Add(m);
            });
        };
    }

    /// <summary>Opens and closes a row's detail area from the item's own <c>IsExpanded</c> flag.
    ///
    /// <para>⚠️ The grid's <c>RowDetailsVisibilityMode</c> is <b>Collapsed</b>, and that is deliberate. The
    /// obvious setting – <c>Visible</c>, with the template collapsing itself per row – reserves the details
    /// height on <b>every</b> row whether or not anything is shown. That left a ~28px strip under each row
    /// that belonged to the row but to no cell: the DataGrid selects on a cell, so a click there hit nothing,
    /// and a selected row was a band with white space around it. Measured, not guessed: colouring cells and
    /// row differently showed 46px of cells inside a 74px row.</para>
    ///
    /// <para>So the mode stays Collapsed – no row reserves anything – and the open state is pushed onto the
    /// realized row here instead. <c>LoadingRow</c> covers scrolling and recycling; the per-item subscription
    /// covers the expander being clicked while the row is on screen.</para></summary>
    private void WireRowDetails(DataGrid grid)
    {
        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is not IExpandableRow item) return;
            // ⚠️ Posted, not assigned here. The grid runs its own details-visibility pass right after
            // LoadingRow and overwrites whatever this event set – which is why an earlier attempt at a
            // per-row style setter looked like it did nothing. One dispatcher turn later the pass is done
            // and the assignment sticks.
            //
            // ⚠️ And the row is re-checked inside the post. Rows are recycled, and during a scan the list
            // changes under them: by the time this runs the row may already show a different device, and
            // applying the old device's state to it would open or close the wrong row.
            var row = e.Row;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(row.DataContext, item)) row.AreDetailsVisible = item.IsExpanded;
            });
            item.PropertyChanged -= OnRowExpansionChanged;
            item.PropertyChanged += OnRowExpansionChanged;
        };

        // Rows are recycled, so a handler left on an item that scrolled away would keep updating a row that
        // now shows a different device.
        grid.UnloadingRow += (_, e) =>
        {
            if (e.Row.DataContext is IExpandableRow item) item.PropertyChanged -= OnRowExpansionChanged;
        };

        void OnRowExpansionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(IExpandableRow.IsExpanded)) return;
            if (sender is not IExpandableRow item) return;
            // global::, because inside this namespace "Avalonia" binds to TikMan.App.Avalonia.
            var row = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(grid)
                          .OfType<DataGridRow>()
                          .FirstOrDefault(r => ReferenceEquals(r.DataContext, item));
            if (row is not null) row.AreDetailsVisible = item.IsExpanded;
        }
    }

    /// <summary>Maps the IPv6 selection back to devices, so the detail pane and every device action keep
    /// working from this view – the rows are addresses, but the actions are about devices.
    ///
    /// <para>⚠️ It must set the <b>marked</b> set too, not just the selected device. The bulk actions read
    /// <c>Marked</c>, which prefers the marked set and only falls back to the selection when that set is
    /// empty. With marks left over from the IPv4 grid, a right-click here ran "set credentials" or
    /// "wake" against the devices marked over there – writing a login to a device the user was not even
    /// looking at.</para></summary>
    private void OnIpv6SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var devices = grid.SelectedItems.OfType<Ipv6Row>().Select(r => r.Device).Distinct().ToList();
        _vm.SetMarked(devices);
        if (devices.Count > 0) _vm.SelectedDevice = devices[0];
    }

    /// <summary>Sets the window's minimum width from the toolbar's measured width, so the last button
    /// (Clear) is always fully on screen with the same margin left and right.
    ///
    /// <para>⚠️ Measured, not a constant. The toolbar's width depends on the label lengths, and those change
    /// with the language – "Zugangsdaten setzen" is far wider than "Set credentials". A hard-coded minimum
    /// is therefore right in exactly one language and clips a button in the others. Runs after the first
    /// layout pass, when Bounds are real, and again whenever the language changes.</para></summary>
    private void ApplyMinimumWidth()
    {
        if (this.FindControl<StackPanel>("ToolbarBlock") is not { } toolbar) return;

        var content = toolbar.Bounds.Width;
        if (content <= 0) return;   // not laid out yet – called again later

        // The 16 px margin the toolbar carries on each side, plus the window chrome the content never gets.
        var chrome = Math.Max(0, Width - (this.FindControl<DockPanel>("RootPanel")?.Bounds.Width ?? Width));
        var wanted = content + 32 + chrome;

        MinWidth = Math.Max(900, Math.Ceiling(wanted));
        if (Width < MinWidth) Width = MinWidth;
        // Never force the window wider/taller than the screen it is on (see ClampToScreen): a minimum beyond
        // the work area pushes window edges – and the scrollbars that live there – off-screen.
        ClampToScreen();
    }

    private void OnDetailSplitterDrag(object? sender, VectorEventArgs e)
    {
        if (_detailPane is null) return;
        // Dragging up must make the pane taller, hence the minus.
        _detailPane.Height = ClampPaneHeight(_detailPane.Height - e.Vector.Y);
        // Remember it: a pane the user sized to their screen should still be that size next time. Marking it
        // "set" also switches off the window-aware default so their exact height is what opens from now on.
        _vm.DetailPaneHeight = _detailPane.Height;
        _vm.DetailPaneHeightSet = true;
    }

    /// <summary>Bounds the detail-pane height. The pane may grow until the device list is down to roughly a
    /// single row (the user wants a big graph/logs pane), but no further – past that the grid loses its header
    /// and horizontal scrollbar and the bottom strip is pushed under the status bar. So ~500 px are always kept
    /// above the pane: the toolbar + banner (~378 worst case, banner shown) + a usable grid (header + a row +
    /// its scrollbar). ⚠️ Deliberately NO 40 % cap: on a tall screen the pane should be allowed to grow large
    /// for the graph/logs; on a short one (720p) the reservation still leaves the grid + scrollbar visible.</summary>
    private double ClampPaneHeight(double wanted)
    {
        var max = Math.Max(70, Bounds.Height - 500);
        return Math.Clamp(wanted, 70, max);
    }

    /// <summary>Picks the pane's OPENING height when the user has never sized it themselves: on a tall window
    /// (&gt; 1000 px) it opens 3× the normal default so a graph/logs pane has room; otherwise the normal
    /// default. Skipped once the user drags the splitter (their exact height is kept and restored) – and also
    /// skipped when the stored height already differs from the shipped default, which is how an install that
    /// customised the pane before this flag existed keeps its value. Not persisted, so the default re-adapts
    /// to whatever window height the app opens at. Runs at Opened, where Bounds.Height is the real size.</summary>
    private void ApplyDefaultDetailPaneHeight()
    {
        if (_detailPane is null || _vm.DetailPaneHeightSet || Math.Abs(_vm.DetailPaneHeight - 270.0) > 0.5) return;
        _detailPane.Height = ClampPaneHeight(Bounds.Height > 1000 ? 3 * 270.0 : 270.0);
    }

    /// <summary>Re-applies the bound after the window changes size – a height dragged large on a big screen
    /// would otherwise stay that size when the window shrinks and push the list out of existence.</summary>
    private void ClampDetailPaneToBounds()
    {
        if (_detailPane is null || Bounds.Height <= 0 || double.IsNaN(_detailPane.Height)) return;
        var clamped = ClampPaneHeight(_detailPane.Height);
        if (Math.Abs(clamped - _detailPane.Height) > 0.5)
        {
            _detailPane.Height = clamped;
            // Only persist when the user actually chose this height. Persisting a clamped AUTO-default would
            // freeze it into the stored value and stop the window-aware default from re-adapting next time.
            if (_vm.DetailPaneHeightSet) _vm.DetailPaneHeight = clamped;
        }
    }

    // ⚠️ Both of these destroy stored state. With "keep device list" on, the entries (and their saved
    // logins) are gone for good – a scan finds the devices again but not their credentials. So both ask
    // first, with Cancel as the default button.
    /// <summary>Checks the marked devices for updates and fills the update columns. Only devices that can
    /// be checked at all (RouterOS with a stored login) are contacted; the rest are skipped silently rather
    /// than reported as failures.</summary>
    private async void OnCheckUpdatesForMarked(object? sender, RoutedEventArgs e)
    {
        // MikroTik is checked through its update API (needs a login); TP-Link/Zyxel have their latest read
        // off the vendor's download page (no login, just a known model).
        var targets = _vm.Marked
            .Where(d => (d.CanUpdate && d.HasLogin) || (FirmwareLatest.IsWebVendor(d.Vendor) && d.Model.Length > 0))
            .Select(d => d.Id).ToList();
        if (targets.Count == 0) { _vm.ReportAction(T("Av_NoUpdatableSelected")); return; }
        await _vm.CheckUpdatesAsync(targets);
    }

    /// <summary>Opens the full IEEE registration behind a device's MAC. Works from the ⓘ button in the
    /// vendor cell (which knows its own row) and from the context menu (which uses the selection).</summary>
    /// <summary>The ⓘ next to the MAC vendor. Marked handled so the click does not also reach the row's
    /// selection/drag handling underneath.</summary>
    private void OnVendorInfoTap(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        OnVendorInfo(sender, e);
    }

    private void OnVendorInfo(object? sender, RoutedEventArgs e)
    {
        var device = DeviceOf(sender as Control) ?? _vm.SelectedDevice;
        if (device is null || device.Mac.Length == 0) { _vm.ReportAction(T("Av_VendorInfoNoMac")); return; }
        new VendorInfoWindow(device.Mac, device.Name).ShowDialog(this);
    }

    private async void OnRemoveDevice(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDevice is not { } d) return;
        if (!await ConfirmWindow.AskAsync(this, T("Av_ConfirmRemove", d.Name), T("Tb_Remove"))) return;
        _vm.RemoveSelected();
    }

    private async void OnClearList(object? sender, RoutedEventArgs e)
    {
        var count = _vm.DeviceCount;
        if (count == 0) return;
        if (!await ConfirmWindow.AskAsync(this, T("Av_ConfirmClear", count), T("Tb_Clear"))) return;
        _vm.ClearList();
    }

    /// <summary>Opens the packet-capture driver's download page. Npcap can't be bundled (its licence
    /// forbids redistribution), so pointing at the installer is the most we can do.</summary>
    private async void OnNpcapClick(object? sender, PointerPressedEventArgs e)
    {
        // ⚠️ On Windows, warn BEFORE opening the download page: Npcap must be installed with "WinPcap
        // API-compatible mode" ticked or the Zyxel ZON discovery silently does nothing. The WPF client
        // shows the same notice; the reminder is easy to miss on the installer's own screen.
        if (OperatingSystem.IsWindows())
        {
            if (!await ConfirmWindow.AskAsync(this, T("Npcap_InstallHint"), T("Npcap_OpenDownload"))) return;
            _vm.OpenPath("https://npcap.com/#download");
        }
        else _vm.OpenPath("https://www.tcpdump.org/");
    }

    private void OnShowMatrix(object? sender, PointerPressedEventArgs e) =>
        new VendorMatrixWindow().Show(this);

    /// <summary>Same window from the menu bar. A separate handler only because a menu click carries
    /// RoutedEventArgs, not the pointer args the banner and the status line hand over.</summary>
    private void OnMatrixMenuClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) =>
        new VendorMatrixWindow().Show(this);

    /// <summary>Opens the vendor matrix when the click landed on the link text at the end of the banner.
    ///
    /// <para>⚠️ Hit-tested by character position rather than by having its own control. The link has to sit
    /// on the same baseline as the sentence it follows, and only real text does that – an embedded control
    /// is laid out as a box and rides visibly high. So the link is a Run, and the click is resolved against
    /// the text layout: the link is the tail of the string, so anything from its first character onwards
    /// counts as a hit.</para></summary>
    private void OnBannerClick(object? sender, PointerPressedEventArgs e)
    {
        var block = this.FindControl<TextBlock>("BannerText");
        if (block is null) return;
        // The handler sits on the banner Border (see the XAML note); the hit test wants the position in
        // the text block's own space.
        if (IsOnBannerLink(e.GetPosition(block))) new VendorMatrixWindow().Show(this);
    }

    /// <summary>Hand cursor over the link only – without it nothing suggests the tail of the sentence is
    /// clickable.</summary>
    private void OnBannerHover(object? sender, PointerEventArgs e)
    {
        var block = this.FindControl<TextBlock>("BannerText");
        if (block is null || sender is not Control container) return;
        container.Cursor = new Cursor(IsOnBannerLink(e.GetPosition(block))
            ? StandardCursorType.Hand
            : StandardCursorType.Arrow);
    }

    private bool IsOnBannerLink(Point point)
    {
        var block = this.FindControl<TextBlock>("BannerText");
        if (block?.TextLayout is not { } layout || block.Inlines is null) return false;

        // ⚠️ The length comes from the Inlines, not from block.Text. In Avalonia those are alternatives:
        // a TextBlock built from Inlines has a null/empty Text, so searching it found nothing and the link
        // was never clickable – exactly the symptom, and invisible from the XAML alone.
        var total = 0;
        foreach (var inline in block.Inlines)
            if (inline is Run run) total += run.Text?.Length ?? 0;

        // The link is the last run, so it occupies the tail of the string.
        var link = T("Av_BannerMatrixLink");
        var start = total - link.Length;
        if (start < 0) return false;

        // ⚠️ Rectangle containment, not HitTestPoint. Diagnosed empirically: HitTestPoint reported
        // IsInside=false even for a point in the middle of the link's first glyph (and the layout counts
        // one more character than the runs hold, so index math is shaky anyway). HitTestTextRange gives the
        // link's own boxes – one per line if it wraps – and a click either lands in one or it is not on the
        // link. The boxes are inflated by a hair so the underline row still counts.
        foreach (var rect in layout.HitTestTextRange(start, link.Length))
            if (rect.Inflate(1).Contains(point))
                return true;
        return false;
    }

    // ---- appearance -------------------------------------------------------------------------------

    private void OnThemeAuto(object? sender, RoutedEventArgs e) => SetTheme(AppTheme.System);
    private void OnThemeLight(object? sender, RoutedEventArgs e) => SetTheme(AppTheme.Light);
    private void OnThemeDark(object? sender, RoutedEventArgs e) => SetTheme(AppTheme.Dark);

    /// <summary>Applies the appearance immediately and remembers it, so the next start comes up the same.</summary>
    private void SetTheme(AppTheme theme)
    {
        App.ApplyTheme(theme);
        _vm.Settings.Theme = theme;
        _vm.SaveSettings();
        MarkTheme(theme);
    }

    /// <summary>Ticks the active entry. The radio marks are set here rather than bound, because the menu
    /// items are created before the settings are read.</summary>
    private void MarkTheme(AppTheme theme)
    {
        if (_themeAutoItem is not null) _themeAutoItem.IsChecked = theme == AppTheme.System;
        if (_themeLightItem is not null) _themeLightItem.IsChecked = theme == AppTheme.Light;
        if (_themeDarkItem is not null) _themeDarkItem.IsChecked = theme == AppTheme.Dark;
    }

    /// <summary>Escape empties a text box – the quickest way to drop a filter or a typed range.</summary>
    /// <summary>Escape clears a box; Enter in the scan-range box starts the scan (the filter box has nothing
    /// to commit – it filters as you type).</summary>
    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        if (e.Key == Key.Escape) { box.Text = ""; e.Handled = true; return; }

        if (e.Key == Key.Enter && box.Name == "RangeBox" && _vm.CanScan)
        {
            _vm.Scan();
            e.Handled = true;
        }
    }
    /// <summary>Shows the action log first, then lets the user open the issue tracker – so a report can
    /// carry evidence, and the user has seen what that evidence contains before sending it.</summary>
    private void OnReportProblem(object? sender, RoutedEventArgs e) => new ReportWindow(_vm).ShowDialog(this);
    private void OnRequestFeature(object? sender, RoutedEventArgs e) => _vm.RequestFeature();
    private void OnBuyCoffee(object? sender, RoutedEventArgs e) => _vm.BuyCoffee();

    // ---- list export ------------------------------------------------------------------------------

    private void OnExportIpv4Csv(object? sender, RoutedEventArgs e) => _ = ExportAsync(ipv6: false, html: false);
    private void OnExportIpv4Html(object? sender, RoutedEventArgs e) => _ = ExportAsync(ipv6: false, html: true);
    private void OnExportIpv6Csv(object? sender, RoutedEventArgs e) => _ = ExportAsync(ipv6: true, html: false);
    private void OnExportIpv6Html(object? sender, RoutedEventArgs e) => _ = ExportAsync(ipv6: true, html: true);

    /// <summary>Writes the current list to CSV or a self-contained HTML table – the IPv6 flavour lists the
    /// devices that actually have IPv6, so the file isn't mostly empty cells.</summary>
    private async Task ExportAsync(bool ipv6, bool html)
    {
        var rows = (ipv6 ? _vm.Ipv6Devices : _vm.Devices).ToList();
        if (rows.Count == 0) { _vm.ReportAction(T("Av_NothingToExport")); return; }

        var ext = html ? "html" : "csv";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"tikman-devices-{(ipv6 ? "ipv6" : "ipv4")}",
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } } },
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // ⚠️ The IPv6 export takes the per-address shape, matching the view. Exporting the device-shaped
            // table from this tab handed the user a different table from the one on screen, with the scope
            // and the per-address services – the reason the view exists – collapsed away.
            // The meta line under the title carries the timestamp and the TikMan version that produced the
            // file, so an exported table stays traceable to a build.
            var appVer = TikMan.Core.AppVersion.Text(typeof(MainWindow).Assembly);
            // ⚠️ TabularExport.Stamp, not a local "HH:mm": the timestamp has to carry its UTC offset, or a
            // file read on another machine cannot say which clock produced it.
            var stamp = TabularExport.Stamp()
                        + (appVer.Length > 0 ? $"  ·  TikMan {appVer}" : "");
            var text = ipv6
                ? (html ? DeviceListExport.Ipv6ToHtml(rows, "TikMan — IPv6 addresses", stamp)
                        : DeviceListExport.Ipv6ToCsv(rows))
                : (html ? DeviceListExport.ToHtml(rows, "Device overview", stamp)
                        : DeviceListExport.ToCsv(rows));
            await System.IO.File.WriteAllTextAsync(path, text);
            _vm.ReportAction(T("Av_Exported", System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex) { _vm.ReportAction(T("Av_SaveFailed", ex.Message)); }
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        // ⚠️ Capture the marked set BEFORE the dialog opens. The 30 s refresh rebuilds the list while the
        // dialog is up, and a login saved against a stale snapshot would silently go nowhere.
        var targets = _vm.Marked.ToList();
        if (targets.Count == 0) return;

        // ⚠️ One vendor at a time. The connection settings below are vendor-specific (a MikroTik speaks
        // REST on 443, a Zyxel only SSH), so applying one choice across a mixed selection would configure
        // most of them wrongly – and the same password across different makes is rarely what was meant.
        var vendors = targets.Select(d => d.Vendor.Trim()).Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (vendors.Count > 1)
        {
            _vm.ReportAction(T("Av_LoginMixedVendors", string.Join(", ", vendors)));
            return;
        }

        var ids = targets.Select(d => d.Id).ToList();
        var title = targets.Count == 1 ? targets[0].Name : T("Av_LoginManyTitle", targets.Count);
        // With several devices there is no single existing username to prefill – fall back to the default.
        var user = targets.Count == 1 ? _vm.LoginUserFor(targets[0]) : _vm.LoginUserFor(null);

        // What TikMan detected, so the user can sanity-check it before configuring anything.
        var first = targets[0];
        var vendorLine = targets.Count == 1
            ? T("Av_DetectedAs", first.Vendor.Length > 0 ? first.Vendor : T("Av_UnknownVendor"),
                first.KindText, first.Model.Length > 0 ? first.Model : "—")
            : T("Av_DetectedAllAs", vendors.Count == 1 ? vendors[0] : T("Av_UnknownVendor"), targets.Count);

        // One vendor for the whole selection is already guaranteed above, so the first device's vendor
        // speaks for all of them – that is what decides which transports the dialog offers.
        var dlg = new LoginWindow(title, user, targets.Count > 1 ? targets.Count : 0,
            _vm.Fleet, targets.Count == 1 ? ids[0] : "", vendorLine, first.Vendor);
        if (await dlg.ShowDialog<LoginResult?>(this) is not { } result) return;

        // The connection choice applies to every marked device – that is the point of doing it here.
        foreach (var id in ids) _vm.Fleet.SetConnection(id, result.Method, result.Port);

        if (ids.Count == 1) _vm.SetLogin(ids[0], result.User, result.Password);
        else _vm.SetLoginForMarked(ids, result.User, result.Password);
    }

    /// <summary>Keeps the view model's marked set in step with the grid. Avalonia's SelectedItems is not
    /// bindable, so the selection has to be pushed across by hand.</summary>
    private void OnDeviceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        _vm.SetMarked(grid.SelectedItems.OfType<DeviceSnapshot>());
        // ⚠️ Deliberately does NOT touch the detail pane. It used to auto-reveal on the first selection, which
        // the pane popping up on a click made annoying. The pane is now purely the user's choice, via the
        // Appearance-menu toggle (and the chevron).
    }

    /// <summary>Appearance-menu toggle: show/hide the detail pane. The menu label is always the action, and
    /// the pane starts hidden for a clean overview.</summary>
    private void OnToggleDetailPane(object? sender, RoutedEventArgs e) =>
        _vm.ShowDetailPane = !_vm.ShowDetailPane;

    /// <summary>Double-click a cell to copy its text – the fastest way to get a MAC or an address out of
    /// the list and into something else.</summary>
    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The visual under the pointer is the cell's TextBlock when the click landed on actual text.
        if (e.Source is TextBlock { Text: { Length: > 0 } text }) await CopyToClipboardAsync(text);
    }

    private async void OnTerminalClick(object? sender, RoutedEventArgs e)
    {
        var device = _vm.SelectedDevice;
        if (device is null) return;

        // ⚠️ Never prompt for credentials INSIDE TikMan. The built-in terminal (SSH.NET) authenticates BEFORE
        // the shell exists, so it can only auto-login with a STORED password – there is no "type it into the
        // terminal" for it. So: a stored login, and the user wants it used (built-in preferred, OR "forward
        // credentials" on) ⇒ built-in terminal, auto-logged-in. Otherwise – no stored login, or the user wants
        // to type it – hand off to the external OS SSH client, which authenticates INTERACTIVELY in its own
        // window (no password on any command line). Opening a terminal never triggers a facts/config read, so
        // a quick manual poke around stays cheap – that is the whole point of not forcing a stored login.
        bool autoLogin = device.HasLogin &&
            (_vm.Settings.PreferBuiltInSsh || _vm.Settings.PassPasswordToExternalClients);
        if (!autoLogin) { _vm.LaunchSsh(device); return; }

        _vm.ReportAction(T("Av_SshConnecting"));
        var result = await _vm.OpenTerminalAsync();        // stored credentials, auto-login
        if (result.Ok)
        {
            _vm.ReportAction("");
            new TerminalWindow(result.Session!, device.Name).Show(this); // non-modal: keep browsing meanwhile
            return;
        }

        // ⚠️ The built-in SSH stack (SSH.NET) could not connect. Old appliance SSH – TP-Link switches in
        // particular – speaks legacy algorithms the managed stack may no longer offer, which shows up here
        // as a connection failure. The external OpenSSH client does offer them (SshCompat re-enables
        // ssh-rsa), so fall back to it rather than leaving the user stuck – unless they explicitly asked
        // for the built-in terminal, in which case silently launching a different client would ignore the
        // setting they just made. Either way the real reason goes to the Connection errors tab.
        if (_vm.Settings.PreferBuiltInSsh) { _vm.ReportAction(T("Av_SshFailed") + " " + result.Error); return; }
        _vm.ReportAction(T("Av_SshFailedReason", result.Error));
        _vm.LaunchSsh(device);
    }

    /// <summary>Opens the built-in VNC viewer, after the one-time-per-click notice if it is switched on.
    ///
    /// <para>⚠️ The notice defaults to "No". The built-in viewer is deliberately simple, and for real work a
    /// current standalone client is both safer and more capable – so an accidental Enter should cancel,
    /// not connect.</para></summary>
    private async void OnVncClick(object? sender, RoutedEventArgs e)
    {
        var device = _vm.SelectedDevice;
        if (device is null || device.VncPort <= 0) return;

        // ⚠️ The third argument is the confirm BUTTON's label, not a dialog title – every other caller
        // passes a verb. Passing the notice's title made the button read "Built-in VNC viewer".
        if (_vm.Settings.ShowVncNotice &&
            !await ConfirmWindow.AskAsync(this, T("Vnc_NoticeText"), T("Av_BtnVnc")))
            return;

        new VncWindow(device.Ip, device.VncPort).Show(this);
    }

    private async void OnBackupClick(object? sender, RoutedEventArgs e)
    {
        _vm.ReportAction(T("Av_BackupRunning"));
        var backup = await _vm.BackupConfigAsync();
        if (!backup.Ok) { _vm.ReportAction(backup.Message); return; }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = backup.FileName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Konfiguration") { Patterns = new[] { "*.rsc", "*.cfg" } },
            },
        });
        if (file is null) { _vm.ReportAction(T("Av_BackupCancelled")); return; }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(backup.Bytes);
            _vm.ReportAction(T("Av_BackupSaved", backup.FileName));
        }
        catch (System.Exception ex) { _vm.ReportAction(T("Av_SaveFailed", ex.Message)); }
    }

    /// <summary>Draws the topology when its tab is selected: the logical map is instant, the physical one
    /// reads the forwarding tables (async, with a loading hint). The device tab needs no work.</summary>
    private async void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tabs is null) return; // fires once during construction, before the fields are set
        // ⚠️ SelectionChanged is a BUBBLING routed event. Every ComboBox and DataGrid inside the tab
        // content (the per-row channel picker in the update assistant, the monitor/log combos, the device
        // grids, the inner detail TabControl) raises it too, and it bubbles up to this TabControl handler.
        // Acting on those re-enters the current tab's logic – e.g. setting a row's channel during
        // UpdateAllView.Reload() bubbled here, called Reload() again, and cleared _rows mid-enumeration
        // ("Collection was modified", crash). Only a genuine tab switch has this TabControl as its source.
        if (!ReferenceEquals(e.Source, _tabs)) return;
        // ⚠️ Switch on the tab's Tag, not its index: tabs come and go (the IPv6 tab is optional), and an
        // index-based switch silently drew the wrong map the moment a tab was inserted above these.
        var tag = (_tabs.SelectedItem as TabItem)?.Tag as string;
        // The detail pane belongs to the device lists only – on the maps/assistants it showed stale device
        // facts under unrelated content. The user's open/closed choice itself is kept (EffectiveShowDetailPane).
        _vm.DetailPaneAllowed = tag is "ipv4" or "ipv6";
        // The assistants pick up devices found since they were last shown.
        if (tag == "backups") { _backupView.Reload(); return; }
        // Opening the update tab refreshes the list and kicks off a check right away, so the
        // Installed/Available columns are current without pressing the button (no-op if one is already running).
        if (tag == "updates") { _updateView.Reload(); _updateView.AutoCheckOnOpen(); return; }
        if (tag is not "logical" and not "physical") return;

        // ⚠️ Reuse an already-built map on a plain tab switch instead of rebuilding it. The physical build
        // now warms every switch's MAC table (traceroute sweep) and re-reads it fresh so the FIRST map is
        // correct – but that is seconds of device load, and doing it on every flick between tabs would be
        // both slow and pointless. So: build once (here, when there is nothing yet), and thereafter only
        // "Rearrange" forces a fresh sweep. A rescan's new devices land on the next rearrange, same as before.
        var existing = tag == "physical" ? _physicalLayout : _logicalLayout;
        if (existing is not null)
        {
            Draw(existing, tag == "physical" ? _physicalCanvas : _logicalCanvas, tag);
            HideOverlay(tag);
            Dispatcher.UIThread.Post(() => FitCanvasWhenReady(tag == "physical" ? _physicalCanvas : _logicalCanvas),
                DispatcherPriority.Background);
            return;
        }

        // ⚠️ One build at a time. Every physical build polls every bridge over SSH/SNMP and traceroutes
        // every host; switching tabs during one started a second full sweep, multiplying the load on the
        // devices – and whichever finished last won, so the older result could end up on screen.
        await BuildTopologyAsync(tag);
    }

    /// <summary>Rebuilds the map from scratch: re-gathers the evidence (forwarding tables, traces) and
    /// re-runs the layout. This is what "rearrange" does – redrawing alone would only reproduce the same
    /// picture, and hand-moved nodes need a way back to the automatic arrangement.</summary>
    private void OnTopoRelayout(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as Control)?.Tag as string ?? "logical";

        // ⚠️ Forget the hand-set positions FIRST. Rebuilding alone did nothing visible: Draw re-applies
        // every saved position on top of the fresh layout (that is what makes a rescan keep the user's
        // arrangement), so "rearrange" recomputed a tidy tree and then put every node straight back where
        // it had been dragged. ClearPositions existed for exactly this and had no caller but "clear
        // everything" – so the button that is supposed to undo a tangle could not.
        // Manual NODES keep their own coordinates (SavePosition mirrors them onto the node itself), so this
        // resets the measured map without moving anything the user added.
        _vm.TopoEdit.ClearPositions(tag);
        _vm.SaveSettings();
        // ⚠️ And the cached forwarding tables go too. Asking for a fresh arrangement is also asking for
        // fresh evidence – rebuilding the same picture from the same cached tables would be a button that
        // redraws without re-measuring, which is what "rearrange" is least likely to mean.
        if (tag == "physical") _vm.Fleet.ClearFdbCache();
        // BuildTopologyAsync re-fits when it finishes, so the pan/zoom goes back to "whole map, centred".
        _ = BuildTopologyAsync(tag);
    }

    private async Task BuildTopologyAsync(string tag)
    {
        // ⚠️ One build at a time. Every physical build polls every bridge over SSH/SNMP and traceroutes
        // every host; switching tabs during one started a second full sweep, multiplying the load on the
        // devices – and whichever finished last won, so the older result could end up on screen.
        if (_topoBuilding) return;
        _topoBuilding = true;
        try
        {
            if (tag == "logical")
            {
                _logicalLayout = _vm.BuildLogicalTopology();
                Draw(_logicalLayout, _logicalCanvas, "logical");
                HideOverlay("logical");
                Dispatcher.UIThread.Post(() => FitCanvasWhenReady(_logicalCanvas), DispatcherPriority.Background);
            }
            else
            {
                // ⚠️ The physical map is built from the credential-based forwarding-table reads, so it can
                // only be drawn once the scan and any device re-reads have finished. Say that explicitly
                // while we wait, instead of a bare "building…" that looks stuck. (The Core build gates on the
                // same condition; this just makes the reason visible.)
                // ⚠️ The overlay is set ONCE, outside the polling loop – rebuilding it every 300 ms re-created
                // its progress control and restarted the animation each time, which read as "the build keeps
                // starting over". And it is a SPINNER, not a bar: waiting-for-a-turn and actually-reading-
                // the-devices now look different (the read phase keeps the bar, see ShowLoading).
                if (_vm.Fleet.Status.Scanning || _vm.Fleet.Rescanning)
                {
                    SetOverlay("physical",
                        new Spinner(),
                        new TextBlock { Text = T("Av_MapWaitReads"), FontWeight = FontWeight.SemiBold,
                                        TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
                    while (_vm.Fleet.Status.Scanning || _vm.Fleet.Rescanning)
                        await Task.Delay(300);
                }
                ShowLoading("physical");
                _physicalLayout = await _vm.BuildPhysicalTopologyAsync();
                Draw(_physicalLayout, _physicalCanvas, "physical");
                HideOverlay("physical");
                Dispatcher.UIThread.Post(() => FitCanvasWhenReady(_physicalCanvas), DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            // The failure goes in the same centred overlay: pinned to the viewport, so it cannot be panned
            // out of sight either.
            (tag == "physical" ? _physicalCanvas : _logicalCanvas).Children.Clear();
            SetOverlay(tag, new TextBlock
            {
                Text = T("Av_MapFailed", ex.Message),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            });
        }
        finally { _topoBuilding = false; }
    }

    /// <summary>All credential-based re-reads just finished (<see cref="FleetService.RescanCompleted"/>). The
    /// maps that reflect that evidence are now stale caches, so drop them; if a topology tab is showing,
    /// rebuild it right away (the gated physical build pulls fresh forwarding tables), otherwise it rebuilds
    /// the next time it is opened. The logical map uses no credentials but its device set may have changed,
    /// so it is refreshed too – it's instant.</summary>
    private void OnRescanCompleted()
    {
        _physicalLayout = null;
        _logicalLayout = null;
        var tag = (_tabs?.SelectedItem as TabItem)?.Tag as string;
        if (tag is "physical" or "logical")
            _ = BuildTopologyAsync(tag);
    }

    /// <summary>A proper "working on it" card instead of a bare line of text – the physical map reads every
    /// bridge's forwarding table, which takes a while and deserves more than a naked sentence.
    ///
    /// <para>⚠️ Drawn into the tab's overlay, NOT onto the canvas. The canvas carries the pan/zoom
    /// transform, so the card used to scroll away under the wheel and shrink with it – and it sat at
    /// (28, 28) rather than in the middle. The overlay is a sibling of the canvas inside the clip border,
    /// so it stays centred in the viewport whatever the user does with the map behind it.</para></summary>
    private void ShowLoading(string tag)
    {
        SetOverlay(tag,
            new TextBlock { Text = T("Av_MapBuilding"), FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = T("Av_MapBuildingHint"), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
            new ProgressBar { IsIndeterminate = true, Height = 5, Margin = new Thickness(0, 4, 0, 0) });
    }

    /// <summary>Fills a tab's centred overlay with the given controls and shows it; no controls hides it.</summary>
    private void SetOverlay(string tag, params Control[] content)
    {
        var physical = tag == "physical";
        var host = this.FindControl<Border>(physical ? "PhysicalOverlay" : "LogicalOverlay");
        var body = this.FindControl<StackPanel>(physical ? "PhysicalOverlayBody" : "LogicalOverlayBody");
        if (host is null || body is null) return;

        body.Children.Clear();
        foreach (var c in content) body.Children.Add(c);
        host.IsVisible = content.Length > 0;
    }

    private void HideOverlay(string tag) => SetOverlay(tag);

    /// <summary>Collision-avoidance for the map: anchored nodes (hand-placed or with a saved position) are
    /// fixed obstacles; each free node (positioned by the automatic layout) that lands on an anchored one is
    /// moved to the nearest clear grid slot. This keeps a saved arrangement intact while a device that has
    /// appeared since it was saved gets a spot of its own instead of overlapping – no rearrange needed.
    /// <para>Free-vs-free never collides (the automatic layout is already tidy), and anchored-vs-anchored is
    /// deliberately left alone – that is the user's own arrangement. A resolved free node then becomes an
    /// obstacle too, so two nodes pushed off the same anchor don't stack on each other.</para></summary>
    private static void NudgeFreeNodesOffAnchored(List<(TopoBox box, bool anchored)> nodes)
    {
        const double gap = 16;
        var obstacles = new List<TopoBox>();
        foreach (var t in nodes) if (t.anchored) obstacles.Add(t.box);

        bool Hits(double x, double y, double w, double h) =>
            obstacles.Any(o => x < o.X + o.W + gap && x + w + gap > o.X &&
                               y < o.Y + o.H + gap && y + h + gap > o.Y);

        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].anchored) continue;
            var b = nodes[i].box;
            if (Hits(b.X, b.Y, b.W, b.H))
            {
                double stepX = b.W + gap, stepY = b.H + gap, bx = b.X, by = b.Y;
                // Spiral outward from the fresh position in node-sized steps to the nearest free slot.
                for (int ring = 1; ring <= 40 && Hits(bx, by, b.W, b.H); ring++)
                    for (int dy = -ring; dy <= ring && Hits(bx, by, b.W, b.H); dy++)
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;   // ring perimeter only
                            double x = b.X + dx * stepX, y = b.Y + dy * stepY;
                            if (!Hits(x, y, b.W, b.H)) { bx = x; by = y; break; }
                        }
                b = b with { X = bx, Y = by };
                nodes[i] = (b, false);
            }
            obstacles.Add(b);
        }
    }

    /// <summary>Renders a topology layout onto a canvas: edges as lines behind the boxes, each node as a
    /// coloured rounded rectangle at its computed position. The colours are the shared hex strings the Core
    /// builder produced, so the map looks the same here, in the web UI and in the WPF/PDF export.</summary>
    /// <summary>Renders a layout onto a canvas. Nodes are draggable and the edges follow them, so a map the
    /// automatic layout got tangled can be sorted out by hand.
    ///
    /// <para>The user's own contributions are folded in here: saved positions override the computed ones
    /// (so a rescan replaces the <i>measurements</i> and leaves the arrangement alone), and manual nodes
    /// and connections are drawn on top – <b>dashed</b>, because they are assertions rather than evidence
    /// and the map must not present the two as equal.</para></summary>
    private void Draw(TopoLayout layout, Canvas canvas, string view)
    {
        canvas.Children.Clear();

        var edit = _vm.TopoEdit;

        // Measured nodes, with any hand-set position applied, plus the user's own nodes. A node the user
        // placed (a saved position, or a manual node) is "anchored"; one the automatic layout positioned is
        // "free".
        // ⚠️ A saved hand-arrangement is reused only while it still describes THIS map. The moment a measured
        // node appears that has no saved spot – a device that joined, a new WLAN/port node, a structural change
        // from a vendor/code update – the saved positions describe a DIFFERENT graph. Applying them to only SOME
        // nodes leaves each newcomer at its fresh-layout coordinate, far from its stale-positioned parent: the
        // exact first-build scramble that only "rearrange" (which clears the positions) used to fix. So honour
        // the arrangement only when it covers EVERY measured node; otherwise draw the fresh tidy layout. Re-drag
        // to re-pin – it sticks until the map's shape changes again. Manual nodes/edges are the user's explicit
        // assertions and are always kept.
        bool fullArrangement = layout.Nodes.Count > 0 &&
            layout.Nodes.All(n => edit.PositionOf(view, n.Key) is not null);
        var tagged = layout.Nodes
            .Select(n => fullArrangement && edit.PositionOf(view, n.Key) is { } p
                ? (box: n with { X = p.X, Y = p.Y }, anchored: true)
                : (box: n, anchored: false))
            .Concat(edit.ManualNodes(view).Select(m => (box: TopoEditing.ToBox(m), anchored: true)))
            .ToList();

        // Keep every hand-placed node exactly where the user put it, but nudge a FREE node off any anchored
        // node it happens to land on. That is what stops a topology which gained a box (a switch added since
        // the arrangement was saved) from drawing it on top of an anchored one – the overlap that used to
        // need a "rearrange" to clear. Anchored-vs-anchored is left untouched: that is the user's own layout.
        NudgeFreeNodesOffAnchored(tagged);

        var nodes = tagged.Select(t => t.box).ToList();

        var byKey = new Dictionary<string, TopoBox>();
        foreach (var n in nodes) byKey[n.Key] = n;

        // Edges are drawn first (so they sit behind the boxes) and remembered per node, so dragging one
        // box can move just the lines attached to it instead of rebuilding the map.
        var edgesOf = new Dictionary<string, List<(Line Line, bool IsStart)>>();
        foreach (var edge in layout.Edges)
        {
            if (!byKey.TryGetValue(edge.From, out var a) || !byKey.TryGetValue(edge.To, out var b)) continue;
            var line = new Line
            {
                StartPoint = new Point(a.X + a.W / 2, a.Y + a.H / 2),
                EndPoint = new Point(b.X + b.W / 2, b.Y + b.H / 2),
                Stroke = Brush("#C4CCD2"),
                StrokeThickness = 1.4,
            };
            canvas.Children.Add(line);

            if (!edgesOf.TryGetValue(edge.From, out var fromList)) edgesOf[edge.From] = fromList = new();
            fromList.Add((line, true));
            if (!edgesOf.TryGetValue(edge.To, out var toList)) edgesOf[edge.To] = toList = new();
            toList.Add((line, false));
        }

        // Hand-drawn connections: dashed and in the manual colour, so nobody mistakes one for a path the
        // forwarding tables actually proved.
        foreach (var edge in edit.ManualEdges(view))
        {
            if (!byKey.TryGetValue(edge.From, out var a) || !byKey.TryGetValue(edge.To, out var b)) continue;
            var line = new Line
            {
                StartPoint = new Point(a.X + a.W / 2, a.Y + a.H / 2),
                EndPoint = new Point(b.X + b.W / 2, b.Y + b.H / 2),
                Stroke = Brush("#78909C"),
                StrokeThickness = 1.6,
                StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            };
            canvas.Children.Add(line);

            if (!edgesOf.TryGetValue(edge.From, out var fromList)) edgesOf[edge.From] = fromList = new();
            fromList.Add((line, true));
            if (!edgesOf.TryGetValue(edge.To, out var toList)) edgesOf[edge.To] = toList = new();
            toList.Add((line, false));
        }

        var manualKeys = edit.ManualNodes(view).Select(n => n.Key).ToHashSet();

        double maxX = 0, maxY = 0;
        foreach (var n in nodes)
        {
            var stack = new StackPanel();
            // ⚠️ The category goes FIRST, above the name. Scanning a map is a two-stage job: work out what
            // kind of thing each box is, then read the ones that matter. With the category buried on the
            // fourth line that first pass meant reading every box top to bottom. As a caption over the name
            // the categories line up down the map and the shape of the network reads in one sweep.
            // Small and quiet on purpose – it labels the box, the name is still the heading.
            if (!string.IsNullOrEmpty(n.Kind))
                stack.Children.Add(new TextBlock { Text = n.Kind, FontSize = 10, Foreground = Brush(n.Text),
                    Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock
            {
                Text = n.Title,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(n.Text),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            // Vendor, model, address – each on its own line, under the name. The point is to read the SHAPE
            // of the network off the map, what each box is and what it is made of, without opening every
            // device. ⚠️ Vendor and model share no line: together they routinely exceed the box width, and
            // since the model sits second it was the half that the ellipsis ate – losing the specific fact.
            if (!string.IsNullOrEmpty(n.Vendor))
                stack.Children.Add(new TextBlock { Text = n.Vendor, FontSize = 11, Foreground = Brush(n.Text),
                    Opacity = 0.9, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(n.Model))
                stack.Children.Add(new TextBlock { Text = n.Model, FontSize = 11, Foreground = Brush(n.Text),
                    Opacity = 0.9, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(n.Detail))
                stack.Children.Add(new TextBlock { Text = n.Detail, FontSize = 11, Foreground = Brush(n.Text), Opacity = 0.85 });

            var box = new Border
            {
                Width = n.W,
                Height = n.H,
                Background = Brush(n.Fill),
                BorderBrush = Brush(n.Line),
                // A thicker, slate outline marks a node the user added: not something TikMan found.
                BorderThickness = new Thickness(manualKeys.Contains(n.Key) ? 2.2 : 1.4),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 6),
                Child = stack,
                Cursor = new Cursor(StandardCursorType.SizeAll),
                Tag = n.Key,
            };

            ToolTip.SetTip(box, manualKeys.Contains(n.Key) ? T("Av_TopoManualNodeTip") : NodeTip(n));
            box.ContextMenu = BuildNodeMenu(view, n.Key, manualKeys.Contains(n.Key));

            AttachNodeDrag(box, canvas, n.Key, edgesOf, view);

            Canvas.SetLeft(box, n.X);
            Canvas.SetTop(box, n.Y);
            canvas.Children.Add(box);

            maxX = Math.Max(maxX, n.X + n.W);
            maxY = Math.Max(maxY, n.Y + n.H);
        }

        canvas.Width = maxX + 40;
        canvas.Height = maxY + 40;

        // The Smart-Connect caveat rides on the physical map only, and only when a Zyxel box is actually on it
        // (that is where the ZLD's port data is a group guess). Kept off the canvas so it stays pinned to the
        // corner while the map pans and zooms.
        if (view == "physical" && _smartConnectHint is not null)
            _smartConnectHint.IsVisible = nodes.Any(n => n.Vendor.Contains("Zyxel", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The Smart-Connect caveat for an export, or "" when it does not apply (logical view, or no
    /// Zyxel device on the map). Same rule as the on-screen caption.</summary>
    private static string SmartConnectHintFor(TopoLayout layout, bool physical) =>
        physical && layout.Nodes.Any(n => n.Vendor.Contains("Zyxel", StringComparison.OrdinalIgnoreCase))
            ? T("Av_TopoSmartConnectHint") : "";

    /// <summary>What a node shows on hover: the parts that don't fit in the box. The MAC is the useful one –
    /// it is what the forwarding tables actually matched on to place the device here.</summary>
    private static string NodeTip(TopoBox n)
    {
        // Same order as the box – category first, then the name.
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(n.Kind)) lines.Add(n.Kind);
        lines.Add(n.Title);
        // Repeated from the box on purpose: a long vendor/model line is ellipsised in the box, and the
        // tooltip is where the untruncated value belongs.
        if (!string.IsNullOrWhiteSpace(n.Vendor)) lines.Add(n.Vendor);
        if (!string.IsNullOrWhiteSpace(n.Model)) lines.Add(n.Model);
        if (!string.IsNullOrWhiteSpace(n.Detail)) lines.Add(n.Detail);
        if (!string.IsNullOrWhiteSpace(n.Mac)) lines.Add(n.Mac);
        return string.Join("\n", lines);
    }

    // Drag state. Kept per gesture rather than per node: only one node moves at a time.
    private Border? _dragNode;
    private Point _dragStart;
    private double _dragOriginLeft, _dragOriginTop;

    /// <summary>Makes one node draggable, moving its attached edge ends with it.
    /// <para>⚠️ The pointer events are handled so the drag does not also pan the canvas underneath – both
    /// live on the same surface, and without this a node drag would move the node <i>and</i> the map.</para></summary>
    private void AttachNodeDrag(Border box, Canvas canvas, string key,
        Dictionary<string, List<(Line Line, bool IsStart)>> edgesOf, string view)
    {
        // ⚠️ One handler, not two. Avalonia skips a subscription when the event is already handled – and
        // that applies to further handlers on the *same* element too. A second PointerPressed registered
        // below this one never ran, because this one sets Handled, which is why "connect from here" could
        // arm but never complete: the click that should have picked the second node only dragged it.
        box.PointerPressed += (_, e) =>
        {
            if (_connectFrom is not null)
            {
                PickConnectionEnd(key, view);
                e.Handled = true;
                return;     // picking ends, and must not also start a drag
            }

            _dragNode = box;
            _dragStart = e.GetPosition(canvas);
            _dragOriginLeft = Canvas.GetLeft(box);
            _dragOriginTop = Canvas.GetTop(box);
            e.Pointer.Capture(box);
            e.Handled = true;
        };

        box.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(_dragNode, box)) return;
            var p = e.GetPosition(canvas);
            var left = _dragOriginLeft + (p.X - _dragStart.X);
            var top = _dragOriginTop + (p.Y - _dragStart.Y);
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);

            var centre = new Point(left + box.Width / 2, top + box.Height / 2);
            if (edgesOf.TryGetValue(key, out var attached))
                foreach (var (line, isStart) in attached)
                    if (isStart) line.StartPoint = centre; else line.EndPoint = centre;

            e.Handled = true;
        };

        box.PointerReleased += (_, e) =>
        {
            if (!ReferenceEquals(_dragNode, box)) return;
            _dragNode = null;
            e.Pointer.Capture(null);

            // ⚠️ Only a node that actually MOVED is pinned. This saved unconditionally, so a plain click –
            // just looking at a node – pinned it at wherever the layout had put it. Click a few nodes while
            // reading the map and most of it is frozen; the automatic arrangement then has no visible
            // effect at all, because Draw re-applies every saved position over the fresh layout. That is
            // why a new layout only appeared after "rearrange", which is the one thing that clears them.
            var left = Canvas.GetLeft(box);
            var top = Canvas.GetTop(box);
            if (Math.Abs(left - _dragOriginLeft) > 2 || Math.Abs(top - _dragOriginTop) > 2)
            {
                // Remember where it was put, so the next scan rebuilds the measurements without undoing the
                // arrangement. Saved on release rather than on every move – one write per drag, not per pixel.
                _vm.TopoEdit.SavePosition(view, key, left, top);
                _vm.SaveSettings();
            }
            e.Handled = true;
        };

    }

    /// <summary>Takes one end of a hand-drawn connection. Called from the node's single PointerPressed
    /// handler while "connect" is armed.</summary>
    private void PickConnectionEnd(string key, string view)
    {
        if (_connectFrom is null) return;
        if (_connectFrom.Length == 0) { _connectFrom = key; _vm.ReportAction(T("Av_TopoPickSecond")); return; }

        // A node cannot be connected to itself; disarm rather than leaving the user stuck in picking mode.
        if (_connectFrom == key) { _connectFrom = null; _vm.ReportAction(T("Av_TopoEdgeRejected")); return; }

        var ok = _vm.TopoEdit.AddEdge(view, _connectFrom, key);
        _connectFrom = null;
        if (ok) { _vm.SaveSettings(); _ = BuildTopologyAsync(view); }
        else _vm.ReportAction(T("Av_TopoEdgeRejected"));
    }

    /// <summary>Non-null while the user is picking the two ends of a connection; empty string means "armed,
    /// waiting for the first node".</summary>
    private string? _connectFrom;

    /// <summary>Adds a node the user names – for equipment TikMan has no way to see. It lands roughly in
    /// the middle of the current view, then it can be dragged where it belongs.</summary>
    private async void OnTopoAddNode(object? sender, RoutedEventArgs e)
    {
        var view = (sender as Control)?.Tag as string ?? "logical";
        var canvas = view == "physical" ? _physicalCanvas : _logicalCanvas;

        var label = await TextPromptWindow.AskAsync(this, T("Av_TopoAddNode"), T("Av_TopoNodeName"));
        if (string.IsNullOrWhiteSpace(label)) return;

        _vm.TopoEdit.AddNode(view, label.Trim(), canvas.Width / 2 - 84, canvas.Height / 2 - 28);
        _vm.SaveSettings();
        await BuildTopologyAsync(view);
    }

    /// <summary>Drops everything the user added to this view – their nodes, their links and their
    /// arrangement – after asking, because it cannot be undone.</summary>
    private async void OnTopoClearManual(object? sender, RoutedEventArgs e)
    {
        var view = (sender as Control)?.Tag as string ?? "logical";
        // ⚠️ Clear BOTH map views, not just the tab the button sits on. The manual nodes/links and the hand
        // arrangement are stored per view, and "discard my additions" is expected to wipe the lot – so nothing
        // is left lingering on the other map. Rebuild the current view now; the other redraws clean when shown.
        if (!_vm.TopoEdit.HasAnything("logical") && !_vm.TopoEdit.HasAnything("physical"))
        { _vm.ReportAction(T("Av_TopoNothingManual")); return; }
        if (!await ConfirmWindow.AskAsync(this, T("Av_TopoClearConfirm"), T("Av_TopoClearManual"))) return;

        _vm.TopoEdit.ClearAll("logical");
        _vm.TopoEdit.ClearAll("physical");
        _vm.SaveSettings();
        await BuildTopologyAsync(view);
    }

    /// <summary>Per-node menu: connect from here, and (for a hand-added node) delete it.</summary>
    private ContextMenu BuildNodeMenu(string view, string key, bool isManual)
    {
        var menu = new ContextMenu();
        var items = new List<Control>();

        var connect = new MenuItem { Header = T("Av_TopoConnectFrom") };
        connect.Click += (_, _) => { _connectFrom = key; _vm.ReportAction(T("Av_TopoPickSecond")); };
        items.Add(connect);

        var unlink = new MenuItem { Header = T("Av_TopoRemoveLinks") };
        unlink.Click += (_, _) =>
        {
            _vm.TopoEdit.RemoveEdgesOf(view, key);
            _vm.SaveSettings();
            _ = BuildTopologyAsync(view);
        };
        items.Add(unlink);

        if (isManual)
        {
            items.Add(new Separator());
            var remove = new MenuItem { Header = T("Av_TopoRemoveNode") };
            remove.Click += (_, _) =>
            {
                _vm.TopoEdit.RemoveNode(view, key);
                _vm.SaveSettings();
                _ = BuildTopologyAsync(view);
            };
            items.Add(remove);
        }

        menu.ItemsSource = items;
        return menu;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    // ---- pan / zoom -------------------------------------------------------------------------------

    private bool _panning;
    private Point _panStart;
    private TranslateTransform? _panTranslate;

    /// <summary>Gets (creating on first use) the canvas's scale+translate transform pair. The group is
    /// [Scale, Translate] so the screen point is scale·canvas + translate – the maths the zoom relies on.</summary>
    private static (ScaleTransform S, TranslateTransform T) Transforms(Canvas c)
    {
        if (c.RenderTransform is TransformGroup g && g.Children.Count == 2
            && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (s, t);
        var ns = new ScaleTransform(1, 1);
        var nt = new TranslateTransform(0, 0);
        var grp = new TransformGroup();
        grp.Children.Add(ns);
        grp.Children.Add(nt);
        c.RenderTransform = grp;
        c.RenderTransformOrigin = RelativePoint.TopLeft;
        return (ns, nt);
    }

    /// <summary>The canvas a topology viewport shows, by the Tag both carry.
    /// <para>⚠️ Not <c>border.Child</c>. The handlers used to reach the canvas that way, so wrapping it in
    /// anything – which happened the moment the status card became an overlay beside it – silently turned
    /// pan, zoom and fit into no-ops. A tag survives the tree changing shape.</para></summary>
    private Canvas? CanvasOf(object? viewport) =>
        (viewport as Control)?.Tag as string == "physical" ? _physicalCanvas
        : (viewport as Control)?.Tag as string == "logical" ? _logicalCanvas
        : null;

    private void OnTopoWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Border b || CanvasOf(b) is not { } c) return;
        var (s, t) = Transforms(c);
        var p = e.GetPosition(b);
        double target = Math.Clamp(s.ScaleX * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 0.1, 8);
        double factor = target / s.ScaleX;
        // Keep the canvas point under the cursor fixed: newTranslate = p − factor·(p − translate).
        t.X = p.X - factor * (p.X - t.X);
        t.Y = p.Y - factor * (p.Y - t.Y);
        s.ScaleX = s.ScaleY = target;
        e.Handled = true;
    }

    private void OnTopoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || CanvasOf(b) is not { } c) return;
        var (_, t) = Transforms(c);
        _panning = true;
        _panStart = e.GetPosition(b);
        _panTranslate = t;
        e.Pointer.Capture(b);
    }

    private void OnTopoMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning || _panTranslate is null || sender is not Border b) return;
        var p = e.GetPosition(b);
        _panTranslate.X += p.X - _panStart.X;
        _panTranslate.Y += p.Y - _panStart.Y;
        _panStart = p;
    }

    private void OnTopoReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panning = false;
        _panTranslate = null;
        e.Pointer.Capture(null);
    }

    private void OnTopoFit(object? sender, RoutedEventArgs e) =>
        FitCanvas((sender as Control)?.Tag as string == "physical" ? _physicalCanvas : _logicalCanvas);

    /// <summary>Scales the graph to fit its viewport (never past 1:1) and centres it.
    ///
    /// <para>⚠️ The viewport is looked up BY NAME, not as <c>c.Parent</c>. The direct parent stopped being
    /// the clip border the moment the canvas was wrapped (the status overlay sits beside it in a Panel now)
    /// and this returned immediately – "Fit" became a button that did nothing at all, silently. A name
    /// survives the tree changing shape; walking up for "some Border" would also match a template part.</para>
    ///
    /// <para>Returns false when the viewport has not been measured yet – a freshly shown tab has Bounds 0,
    /// and the caller retries rather than leaving the map pinned to the top-left corner.</para></summary>
    private bool FitCanvas(Canvas c)
    {
        var b = this.FindControl<Border>(
            ReferenceEquals(c, _physicalCanvas) ? "PhysicalViewport" : "LogicalViewport");
        if (b is null) return false;
        double vw = b.Bounds.Width, vh = b.Bounds.Height, cw = c.Width, ch = c.Height;
        if (vw <= 0 || vh <= 0 || cw <= 0 || ch <= 0) return false;
        var (s, t) = Transforms(c);
        double scale = Math.Min(Math.Min(vw / cw, vh / ch), 1.0);
        s.ScaleX = s.ScaleY = scale;
        t.X = (vw - cw * scale) / 2;
        t.Y = (vh - ch * scale) / 2;
        return true;
    }

    /// <summary>Fits as soon as the viewport has a size. ⚠️ One posted attempt was not enough: on a tab
    /// being shown for the first time the bounds are still 0 a dispatcher turn later, the fit was skipped,
    /// and the map stayed stuck in the corner until the user resized the window. A few short retries cost
    /// nothing and cover the layout pass whenever it lands.</summary>
    private void FitCanvasWhenReady(Canvas c, int attemptsLeft = 12)
    {
        if (FitCanvas(c) || attemptsLeft <= 0) return;
        DispatcherTimer.RunOnce(() => FitCanvasWhenReady(c, attemptsLeft - 1), TimeSpan.FromMilliseconds(40));
    }

    // ---- export (PNG raster / PDF vector) ---------------------------------------------------------

    private async void OnTopoExport(object? sender, RoutedEventArgs e)
    {
        bool physical = (sender as Control)?.Tag as string == "physical";
        var layout = physical ? _physicalLayout : _logicalLayout;
        if (layout is null || layout.Nodes.Count == 0) { _vm.ReportAction(T("Av_NoMapExport")); return; }

        // ⚠️ No DefaultExtension on purpose. Setting it pinned every save to ".pdf" even when the user picked
        // the PNG file type, and the dispatch below goes by extension – so "PNG" silently produced a PDF.
        // Without it the picker appends the extension of the chosen type, which is what makes the choice real.
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            // ⚠️ The logical view is the IP-distribution map, and the file it saves has to be called that.
            // "topology-logical" was left over from when both tabs were called Topology, and it landed in
            // the user's folder as a second file that looked like another copy of the physical map.
            SuggestedFileName = physical ? "topology-physical" : "ip-distribution",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } },
                new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } },
                // GraphML: the map as data rather than a picture – opens in yEd, Gephi, Cytoscape,
                // NetworkX. Keeps the device facts and the discovered/manual distinction.
                new FilePickerFileType("GraphML") { Patterns = new[] { "*.graphml" } },
                // draw.io: the same map as an editable diagram, for carrying on by hand.
                new FilePickerFileType("draw.io") { Patterns = new[] { "*.drawio", "*.xml" } },
            },
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        // A platform that appends nothing would leave us guessing; make that explicit rather than silent.
        if (!System.IO.Path.HasExtension(path)) path += ".pdf";
        try
        {
            var hint = SmartConnectHintFor(layout, physical);
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ExportPng(layout, path, hint);
            else if (path.EndsWith(".graphml", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".drawio", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var view = physical ? "physical" : "logical";
                // Both exports take the map as it is on screen – the user's positions and their own nodes –
                // with the manual items listed separately so each file can mark them as asserted.
                var shown = WithUserPositions(layout, view);
                var manualNodes = _vm.TopoEdit.ManualNodes(view).Select(n => n.Key).ToList();
                var manualLinks = _vm.TopoEdit.ManualEdges(view).Select(e2 => (e2.From, e2.To)).ToList();

                var xml = path.EndsWith(".graphml", StringComparison.OrdinalIgnoreCase)
                    ? GraphMlExport.Build(shown, view, manualNodes, manualLinks)
                    : DrawIoExport.Build(shown, view, manualNodes, manualLinks);
                await System.IO.File.WriteAllTextAsync(path, xml);
            }
            else ExportPdf(layout, path, hint);
            _vm.ReportAction(T("Av_MapExported", System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex) { _vm.ReportAction(T("Av_ExportFailed", ex.Message)); }
    }

    /// <summary>The layout as it is on screen: measured nodes at whatever position the user gave them, plus
    /// the nodes they added. Exports should match what the user is looking at, not the raw computation.</summary>
    private TopoLayout WithUserPositions(TopoLayout layout, string view)
    {
        var edit = _vm.TopoEdit;
        var nodes = layout.Nodes
            .Select(n => edit.PositionOf(view, n.Key) is { } p ? n with { X = p.X, Y = p.Y } : n)
            .Concat(edit.ManualNodes(view).Select(TopoEditing.ToBox))
            .ToList();
        return new TopoLayout(nodes, layout.Edges);
    }

    private const double ExportPad = 24;

    private static (double W, double H) GraphSize(TopoLayout layout)
    {
        double mx = 0, my = 0;
        foreach (var n in layout.Nodes) { mx = Math.Max(mx, n.X + n.W); my = Math.Max(my, n.Y + n.H); }
        return (mx + 2 * ExportPad, my + 2 * ExportPad);
    }

    /// <summary>Renders the whole graph to a PNG via a RenderTargetBitmap + DrawingContext – independent of
    /// the on-screen zoom/pan, so it always captures the full map at 1:1.</summary>
    private static void ExportPng(TopoLayout layout, string path, string hint = "")
    {
        var (w, h) = GraphSize(layout);
        var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w), (int)Math.Ceiling(h)), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.White, new Rect(0, 0, w, h));
            if (hint.Length > 0)   // quiet italic-grey caveat in the top margin, above the first node
                ctx.DrawText(Formatted(hint, 11, false, "#9AA0A6", w - 2 * ExportPad, italic: true), new Point(ExportPad, 5));
            var byKey = layout.Nodes.ToDictionary(n => n.Key);
            var edgePen = new Pen(Brush("#C4CCD2"), 1.4);
            foreach (var edge in layout.Edges)
            {
                if (!byKey.TryGetValue(edge.From, out var a) || !byKey.TryGetValue(edge.To, out var b)) continue;
                ctx.DrawLine(edgePen,
                    new Point(a.X + a.W / 2 + ExportPad, a.Y + a.H / 2 + ExportPad),
                    new Point(b.X + b.W / 2 + ExportPad, b.Y + b.H / 2 + ExportPad));
            }
            foreach (var n in layout.Nodes)
            {
                var rect = new Rect(n.X + ExportPad, n.Y + ExportPad, n.W, n.H);
                ctx.DrawRectangle(Brush(n.Fill), new Pen(Brush(n.Line), 1.4), rect, 8, 8);
                // Same five lines in the same order as on screen – category first – because an exported map
                // that reads differently from the one it came from is the wrong artefact to hand anyone.
                // ⚠️ Including the per-line shading. The canvas sets an opacity per TextBlock; here the same
                // effect comes from blending toward the fill (TopoColours), because a FormattedText carries
                // a brush rather than an opacity. Without it the raster export came out flat.
                string Ink(double fade) => TikMan.Core.Discovery.TopoColours.Fade(n.Text, n.Fill, fade);
                var ty = rect.Y + 6;
                if (!string.IsNullOrEmpty(n.Kind))
                {
                    ctx.DrawText(Formatted(n.Kind, 10, false, Ink(TikMan.Core.Discovery.TopoColours.KindFade), n.W - 16),
                        new Point(rect.X + 9, ty));
                    ty += 15;
                }
                ctx.DrawText(Formatted(n.Title, 12, true, n.Text, n.W - 16), new Point(rect.X + 9, ty));
                if (!string.IsNullOrEmpty(n.Vendor))
                    ctx.DrawText(Formatted(n.Vendor, 11, false, Ink(TikMan.Core.Discovery.TopoColours.HardwareFade), n.W - 16),
                        new Point(rect.X + 9, ty += 18));
                if (!string.IsNullOrEmpty(n.Model))
                    ctx.DrawText(Formatted(n.Model, 11, false, Ink(TikMan.Core.Discovery.TopoColours.HardwareFade), n.W - 16),
                        new Point(rect.X + 9, ty += 16));
                if (!string.IsNullOrEmpty(n.Detail))
                    ctx.DrawText(Formatted(n.Detail, 11, false, Ink(TikMan.Core.Discovery.TopoColours.DetailFade), n.W - 16),
                        new Point(rect.X + 9, ty + 16));
            }
        }
#pragma warning disable CS0618 // Save(string) is fine for a plain PNG; the encoder-options overload is overkill here.
        rtb.Save(path);
#pragma warning restore CS0618
    }

    private static FormattedText Formatted(string text, double size, bool bold, string colorHex, double maxWidth, bool italic = false) =>
        // ⚠️ MaxTextHeight caps it to ONE line. Without it a FormattedText WRAPS at MaxTextWidth (trimming only
        // ellipsises each line AFTER wrapping) – so a long metadata chain spilled onto a second line that the
        // fixed per-field y-step then drew the next field on top of: the "letters overlapping" in the PNG.
        // Newlines stripped for the same reason – one field is always one line.
        new((text ?? "").Replace('\r', ' ').Replace('\n', ' '), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            size, Brush(colorHex))
        { MaxTextWidth = Math.Max(1, maxWidth), MaxTextHeight = size * 1.6, Trimming = TextTrimming.CharacterEllipsis };

    /// <summary>Renders the whole graph to a true vector PDF via PdfSharp – same primitives and colours as
    /// the WPF export, drawn straight from the layout data.</summary>
    private static void ExportPdf(TopoLayout layout, string path, string hint = "")
    {
        PdfExportFonts.EnsureRegistered();
        var (w, h) = GraphSize(layout);

        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(w);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(h);
        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);

        gfx.DrawRectangle(new PdfSharp.Drawing.XSolidBrush(PdfSharp.Drawing.XColor.FromArgb(255, 255, 255, 255)), 0, 0, w, h);
        if (hint.Length > 0)   // same quiet italic-grey caveat as the PNG / on-screen caption, in the top margin
        {
            var hintFont = new PdfSharp.Drawing.XFont("Arial", 8.5, PdfSharp.Drawing.XFontStyleEx.Italic);
            gfx.DrawString(hint, hintFont, new PdfSharp.Drawing.XSolidBrush(Xc("#9AA0A6")),
                new PdfSharp.Drawing.XPoint(ExportPad, 13));
        }

        var byKey = layout.Nodes.ToDictionary(n => n.Key);
        var edgePen = new PdfSharp.Drawing.XPen(Xc("#C4CCD2"), 1.4);
        foreach (var edge in layout.Edges)
        {
            if (!byKey.TryGetValue(edge.From, out var a) || !byKey.TryGetValue(edge.To, out var b)) continue;
            gfx.DrawLine(edgePen, a.X + a.W / 2 + ExportPad, a.Y + a.H / 2 + ExportPad,
                b.X + b.W / 2 + ExportPad, b.Y + b.H / 2 + ExportPad);
        }

        var titleFont = new PdfSharp.Drawing.XFont("Arial", 9, PdfSharp.Drawing.XFontStyleEx.Bold);
        var detailFont = new PdfSharp.Drawing.XFont("Arial", 7.5);
        foreach (var n in layout.Nodes)
        {
            double x = n.X + ExportPad, y = n.Y + ExportPad;
            gfx.DrawRoundedRectangle(new PdfSharp.Drawing.XPen(Xc(n.Line), 1),
                new PdfSharp.Drawing.XSolidBrush(Xc(n.Fill)), x, y, n.W, n.H, 12, 12);
            gfx.Save();
            gfx.IntersectClip(new PdfSharp.Drawing.XRect(x + 6, y, Math.Max(1, n.W - 12), n.H));
            var text = new PdfSharp.Drawing.XSolidBrush(Xc(n.Text));
            // ⚠️ One brush per line, blended toward the box fill. PdfSharp draws with a brush and has no
            // per-string opacity, so every line used to come out at full strength – the PDF read heavier
            // and flatter than the map it was exported from, with the category shouting as loudly as the
            // name. TopoColours holds the amounts, shared with the canvas and the other exports.
            PdfSharp.Drawing.XSolidBrush Ink(double fade) =>
                new(Xc(TikMan.Core.Discovery.TopoColours.Fade(n.Text, n.Fill, fade)));

            // Five lines in the canvas order – category first. The clip above keeps a long value in its box.
            var ty = y + 12;
            if (!string.IsNullOrEmpty(n.Kind))
            {
                gfx.DrawString(n.Kind, detailFont, Ink(TikMan.Core.Discovery.TopoColours.KindFade),
                    new PdfSharp.Drawing.XPoint(x + 8, ty));
                ty += 13;
            }
            gfx.DrawString(n.Title, titleFont, text, new PdfSharp.Drawing.XPoint(x + 8, ty));
            if (!string.IsNullOrEmpty(n.Vendor))
                gfx.DrawString(n.Vendor, detailFont, Ink(TikMan.Core.Discovery.TopoColours.HardwareFade),
                    new PdfSharp.Drawing.XPoint(x + 8, ty += 14));
            if (!string.IsNullOrEmpty(n.Model))
                gfx.DrawString(n.Model, detailFont, Ink(TikMan.Core.Discovery.TopoColours.HardwareFade),
                    new PdfSharp.Drawing.XPoint(x + 8, ty += 12));
            if (!string.IsNullOrEmpty(n.Detail))
                gfx.DrawString(n.Detail, detailFont, Ink(TikMan.Core.Discovery.TopoColours.DetailFade),
                    new PdfSharp.Drawing.XPoint(x + 8, ty + 13));
            gfx.Restore();
        }
        doc.Save(path);
    }

    private static PdfSharp.Drawing.XColor Xc(string hex)
    {
        var c = Color.Parse(hex);
        return PdfSharp.Drawing.XColor.FromArgb(c.A, c.R, c.G, c.B);
    }
}
