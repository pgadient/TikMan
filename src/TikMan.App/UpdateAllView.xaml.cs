using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TikMan.Core.Api;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>One entry of the channel dropdown: "2026-06-02  7.23.1  (stable)" – the date and version
/// you would get, next to the channel that offers it. Everything the old separate "Latest" column
/// said, in the place where you choose.</summary>
public class ChannelChoice : INotifyPropertyChanged
{
    public string Channel { get; }

    public ChannelChoice(string channel)
    {
        Channel = channel;
        _display = channel;   // until the release info lands (or if we're offline)
    }

    private string _display;
    public string Display { get => _display; private set { _display = value; Notify(nameof(Display)); } }

    public void Describe(string version, string releaseDate) =>
        Display = $"{releaseDate}  {version}  ({Channel})";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Row in the update assistant.</summary>
public class UpdateItemViewModel : INotifyPropertyChanged
{
    public DeviceViewModel Device { get; }

    public UpdateItemViewModel(DeviceViewModel device)
    {
        Device = device;
        _isSelected = device.UpdateAvailable;
        _stateText = device.UpdateAvailable ? T("Ua_StateUpdateAvail") : device.UpdateStatusText;
        _channel = device.UpdateChannel.Length > 0 ? device.UpdateChannel : "stable";
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(nameof(IsSelected)); }
    }

    private string _channel;
    /// <summary>Per-device channel chosen in the update view (used unless "use default channel" is on).</summary>
    public string Channel
    {
        get => _channel;
        set { _channel = value; Notify(nameof(Channel)); }
    }

    private bool _channelEnabled = true;
    /// <summary>False while "use default channel" is active (per-row combo disabled).</summary>
    public bool ChannelEnabled
    {
        get => _channelEnabled;
        set { _channelEnabled = value; Notify(nameof(ChannelEnabled)); }
    }

    private bool _canSelect = true;
    public bool CanSelect
    {
        get => _canSelect;
        set { _canSelect = value; Notify(nameof(CanSelect)); }
    }

    private string _stateText;
    public string StateText
    {
        get => _stateText;
        set { _stateText = value; Notify(nameof(StateText)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Update assistant: pick which devices to update, in which order, on which channel.
/// <para>Lives directly in the Updates tab rather than behind a button. Without a dialog's lifetime to
/// hang them off, the two things the caller used to do around ShowDialog() – pausing the poll timer and
/// persisting the order – now hang off <see cref="RunningChanged"/>.</para></summary>
public partial class UpdateAllView : UserControl
{
    private static readonly TimeSpan RebootPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RebootTimeout = TimeSpan.FromMinutes(10);

    private readonly ObservableCollection<UpdateItemViewModel> _items = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _useDefaultChannel;
    private string _defaultChannel = "stable";

    public UpdateAllView()
    {
        InitializeComponent();
        ItemGrid.ItemsSource = _items;
        foreach (var c in AllChannels) Channels.Add(new ChannelChoice(c));
        _ = LoadChannelVersionsAsync();
    }

    private static readonly string[] AllChannels = { "stable", "long-term", "testing", "development" };

    /// <summary>The channel dropdown's entries. Bound by both the per-device column and the "same channel
    /// for all" box, so the version and date only have to be fetched once – they come from MikroTik's
    /// public upgrade server and are the same for every device.</summary>
    public ObservableCollection<ChannelChoice> Channels { get; } = new();

    /// <summary>Fills in each channel's version and release date. Read-only, and off the public server –
    /// it touches no device and changes nothing. That matters: a device's channel is only ever written
    /// when you actually install (see <see cref="UpdateDeviceAsync"/>), so merely reading this list must
    /// not be what commits you to one.</summary>
    private async Task LoadChannelVersionsAsync()
    {
        foreach (var choice in Channels)
        {
            try
            {
                var info = await ReleaseInfoClient.GetLatestAsync(choice.Channel);
                if (info is { } r) choice.Describe(r.Version, r.ReleaseDate.ToString("yyyy-MM-dd"));
            }
            catch (Exception) { /* offline: the entry keeps the bare channel name */ }
        }
    }

    /// <summary>Raised when a run starts (true) and ends (false). The host pauses its poll timer for the
    /// duration – a monitoring query in the middle of a reboot is noise at best – and writes the
    /// (possibly reordered) list back when it's over.</summary>
    public event EventHandler<bool>? RunningChanged;

    /// <summary>True while updates are being installed.</summary>
    public bool IsRunning => _running;

    /// <summary>Devices in the (possibly reordered) list order – the host persists this.</summary>
    public IReadOnlyList<DeviceViewModel> OrderedDevices => _items.Select(i => i.Device).ToList();

    /// <summary>(Re)fills the list, called each time the tab is shown, since devices and their versions
    /// change with every scan. Ignored while a run is going; rows for devices that are still here keep
    /// the selection, channel and order the user gave them.</summary>
    public void Load(IReadOnlyList<DeviceViewModel> candidates)
    {
        if (_running) return;

        var previous = _items.ToDictionary(i => i.Device);
        _items.Clear();
        foreach (var device in candidates)
            _items.Add(previous.TryGetValue(device, out var old) ? old : new UpdateItemViewModel(device));

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UseDefaultChannel_Changed(object sender, RoutedEventArgs e)
    {
        var useDefault = UseDefaultChannelCheck.IsChecked == true;
        foreach (var item in _items) item.ChannelEnabled = !useDefault;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (_running) return;
        if (ItemGrid.SelectedItem is not UpdateItemViewModel item) return;
        int index = _items.IndexOf(item);
        int target = index + delta;
        if (target < 0 || target >= _items.Count) return;
        _items.Move(index, target);
        ItemGrid.SelectedItem = item;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log(T("Ua_StopRequested"));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log(T("Ua_NoneSelected"));
            return;
        }

        var answer = MessageBox.Show(Window.GetWindow(this)!, T("Ua_ConfirmBody", selected.Count),
            T("Ua_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _running = true;
        _cts = new CancellationTokenSource();
        SetUiRunning(true);
        RunningChanged?.Invoke(this, true);

        bool waitForOnline = WaitForOnlineCheck.IsChecked == true;
        bool continueOnError = ContinueOnErrorCheck.IsChecked == true;
        _useDefaultChannel = UseDefaultChannelCheck.IsChecked == true;
        _defaultChannel = DefaultChannelCombo.SelectedValue as string ?? "stable";
        int done = 0, failed = 0;

        UpdateProgress.Minimum = 0;
        UpdateProgress.Maximum = selected.Count;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;

        try
        {
            foreach (var item in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var ok = await UpdateDeviceAsync(item, waitForOnline, _cts.Token);
                if (ok) done++;
                else
                {
                    failed++;
                    if (!continueOnError)
                    {
                        Log(T("Ua_AbortOnError"));
                        break;
                    }
                }
                UpdateProgress.Value += 1;
            }
            Log(T("Ua_Done", done, failed));
        }
        catch (OperationCanceledException)
        {
            Log(T("Ua_Stopped"));
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
            SetUiRunning(false);
            UpdateProgress.Visibility = Visibility.Collapsed;
            RunningChanged?.Invoke(this, false);
        }
    }

    private async Task<bool> UpdateDeviceAsync(UpdateItemViewModel item, bool waitForOnline, CancellationToken ct)
    {
        var device = item.Device;
        Log(T("Ua_DeviceHeader", device.Name, device.Host));

        // The channel is only ever written here, at install time – picking one in the dropdown reads
        // from MikroTik's public server and leaves the device alone. Say which way it went: a run that
        // silently re-points a device at another channel is a change worth seeing in the log.
        var channel = _useDefaultChannel ? _defaultChannel : item.Channel;
        var already = string.Equals(device.UpdateChannel, channel, StringComparison.OrdinalIgnoreCase);
        item.StateText = T("Ua_StateCheck");
        Log(already ? T("Ua_ChannelAlready", channel) : T("Ua_ChannelSwitched", device.UpdateChannel, channel));
        if (!await device.SetChannelAsync(channel, ct))
        {
            item.StateText = T("Ua_StateCheckFailed", device.LastError);
            Log(T("Ua_CheckFailed", device.LastError));
            return false;
        }
        if (!device.UpdateAvailable)
        {
            item.StateText = T("Ua_StateCurrent");
            item.IsSelected = false;
            Log(T("Ua_AlreadyCurrent"));
            return true;
        }

        item.StateText = T("Ua_StateInstalling", device.LatestVersion);
        Log(T("Ua_Installing", device.LatestVersion));
        try
        {
            await device.InstallUpdateAsync(ct);
        }
        catch (Exception ex)
        {
            item.StateText = T("Ua_StateInstallFailed", ex.Message);
            Log(T("Ua_InstallFailed", ex.Message));
            return false;
        }

        if (!waitForOnline)
        {
            item.StateText = T("Ua_StateStarted");
            Log(T("Ua_StartedNoWait"));
            return true;
        }

        item.StateText = T("Ua_StateWaiting");
        var deadline = DateTime.UtcNow + RebootTimeout;
        await Task.Delay(TimeSpan.FromSeconds(20), ct); // wait out the download phase before polling

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await device.RefreshAsync(ct))
            {
                item.StateText = T("Ua_StateDone", device.Version);
                Log(T("Ua_BackOnline", device.Name, device.Version));
                await device.CheckUpdateAsync(ct); // reset update status/flag
                return true;
            }
            item.StateText = T("Ua_StateWaitingSec", (int)(deadline - DateTime.UtcNow).TotalSeconds);
            await Task.Delay(RebootPollInterval, ct);
        }

        item.StateText = T("Ua_StateTimeout");
        Log(T("Ua_Timeout", device.Name, RebootTimeout.TotalMinutes.ToString("0")));
        return false;
    }

    private void SetUiRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        UpButton.IsEnabled = !running;
        DownButton.IsEnabled = !running;
        UseDefaultChannelCheck.IsEnabled = !running;
        foreach (var item in _items)
        {
            item.CanSelect = !running;
            item.ChannelEnabled = !running && UseDefaultChannelCheck.IsChecked != true;
        }
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        LogBox.ScrollToEnd();
    }
}
