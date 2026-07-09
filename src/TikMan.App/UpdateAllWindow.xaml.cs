using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>Row in the update assistant.</summary>
public class UpdateItemViewModel : INotifyPropertyChanged
{
    public DeviceViewModel Device { get; }

    public UpdateItemViewModel(DeviceViewModel device)
    {
        Device = device;
        _isSelected = device.UpdateAvailable;
        _stateText = device.UpdateAvailable ? T("Ua_StateUpdateAvail") : device.UpdateStatusText;
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(nameof(IsSelected)); }
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

public partial class UpdateAllWindow : Window
{
    private static readonly TimeSpan RebootPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RebootTimeout = TimeSpan.FromMinutes(10);

    private readonly ObservableCollection<UpdateItemViewModel> _items = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public UpdateAllWindow(List<DeviceViewModel> candidates)
    {
        InitializeComponent();
        foreach (var device in candidates)
            _items.Add(new UpdateItemViewModel(device));
        ItemGrid.ItemsSource = _items;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            Log(T("Ua_CloseWhileRunning"));
        }
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

        var answer = MessageBox.Show(this, T("Ua_ConfirmBody", selected.Count),
            T("Ua_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _running = true;
        _cts = new CancellationTokenSource();
        SetUiRunning(true);

        bool waitForOnline = WaitForOnlineCheck.IsChecked == true;
        bool continueOnError = ContinueOnErrorCheck.IsChecked == true;
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
        }
    }

    private async Task<bool> UpdateDeviceAsync(UpdateItemViewModel item, bool waitForOnline, CancellationToken ct)
    {
        var device = item.Device;
        Log(T("Ua_DeviceHeader", device.Name, device.Host));

        // If not checked yet: fetch the update status first
        if (!device.UpdateAvailable)
        {
            item.StateText = T("Ua_StateCheck");
            Log(T("Ua_Checking"));
            if (!await device.CheckUpdateAsync(ct))
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
        CloseButton.IsEnabled = !running;
        UpButton.IsEnabled = !running;
        DownButton.IsEnabled = !running;
        foreach (var item in _items) item.CanSelect = !running;
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        LogBox.ScrollToEnd();
    }
}
