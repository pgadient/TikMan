using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>Row in the scan result list.</summary>
public class ScanResultViewModel : INotifyPropertyChanged
{
    public DiscoveredDevice Discovered { get; }
    public bool AlreadyAdded { get; }

    public ScanResultViewModel(DiscoveredDevice discovered, bool alreadyAdded)
    {
        Discovered = discovered;
        AlreadyAdded = alreadyAdded;
        _isSelected = !alreadyAdded && discovered.IsLikelyMikroTik;
    }

    public string IpAddress => Discovered.IpAddress;
    public string Identity => Discovered.Identity;
    public string MacAddress => Discovered.MacAddress;
    public string Vendor => OuiLookup.Lookup(Discovered.MacAddress);
    /// <summary>Open ports rendered as service names, e.g. "ssh, http, smb".</summary>
    public string Services => string.Join(", ", Discovered.OpenPorts.Select(SubnetScanner.ServiceName));
    /// <summary>Guessed device kind from vendor + open ports (shown in the "Type" column).</summary>
    public string DeviceType => DeviceViewModel.DeviceKindText(DeviceClassifier.Guess(Vendor, Discovered.OpenPorts));
    public bool HasSmb => Discovered.OpenPorts.Contains(445);
    public string Board => Discovered.Board;
    public string Version => Discovered.Version;
    public string Source => Discovered.Source;
    public bool CanSelect => !AlreadyAdded;

    public string Hint
    {
        get
        {
            if (AlreadyAdded) return T("Sc_HintAlreadyAdded");
            if (Discovered.IsLikelyMikroTik && Discovered.Source == "Scan") return T("Sc_HintLikely");
            return "";
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(nameof(IsSelected)); }
    }

    /// <summary>SMB shares of this host (lazily filled when the row is expanded).</summary>
    public ObservableCollection<SmbShareVm> Shares { get; } = new();

    private string _sharesStatus = "";
    public string SharesStatus { get => _sharesStatus; private set { _sharesStatus = value; Notify(nameof(SharesStatus)); } }

    private bool _sharesLoaded;

    /// <summary>Enumerates the host's SMB shares once, on first expand.</summary>
    public async Task LoadSharesAsync()
    {
        if (!HasSmb || _sharesLoaded) return;
        _sharesLoaded = true;
        SharesStatus = T("Sc_SmbLoading");
        try
        {
            var names = await SmbShares.ListAsync(IpAddress);
            foreach (var name in names) Shares.Add(new SmbShareVm(IpAddress, name));
            SharesStatus = names.Count > 0 ? "" : T("Sc_SmbNone");
        }
        catch
        {
            SharesStatus = T("Sc_SmbNone");
        }
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A single SMB share with the UNC path to open in Explorer.</summary>
public class SmbShareVm
{
    public SmbShareVm(string host, string name)
    {
        Name = name;
        UncPath = $@"\\{host}\{name}";
    }

    public string Name { get; }
    public string UncPath { get; }
}

public partial class ScanWindow : Window
{
    private readonly List<Device> _knownDevices;
    private readonly string _defaultUsername;
    private readonly string _defaultEncryptedPassword;
    private readonly ObservableCollection<ScanResultViewModel> _results = new();
    private readonly ObservableCollection<ScanResultViewModel> _resultsV6 = new();
    private CancellationTokenSource? _cts;
    private bool _ipv6Running;

    /// <summary>After DialogResult == true: the devices to be created.</summary>
    public List<Device> NewDevices { get; } = new();

    public ScanWindow(List<Device> knownDevices, string defaultUsername, string defaultEncryptedPassword)
    {
        InitializeComponent();
        _knownDevices = knownDevices;
        _defaultUsername = defaultUsername;
        _defaultEncryptedPassword = defaultEncryptedPassword;
        ResultGrid.ItemsSource = _results;
        ResultGridV6.ItemsSource = _resultsV6;
        SubnetBox.Text = GuessLocalSubnet();
    }

    // ----- IPv6 -----

    private async void Ipv6Discover_Click(object sender, RoutedEventArgs e)
    {
        if (_ipv6Running) return;
        _ipv6Running = true;
        Ipv6Button.IsEnabled = false;
        Ipv6Progress.Visibility = Visibility.Visible;
        Ipv6StatusText.Text = T("Sc_Ipv6Running");
        try
        {
            var found = await Ipv6Discovery.DiscoverAsync(new Progress<DiscoveredDevice>(AddV6Result));
            Ipv6StatusText.Text = T("Sc_Ipv6Done", found.Count);
        }
        catch (Exception ex)
        {
            Ipv6StatusText.Text = T("Sc_Ipv6Error", ex.Message);
        }
        finally
        {
            Ipv6Progress.Visibility = Visibility.Collapsed;
            Ipv6Button.IsEnabled = true;
            _ipv6Running = false;
        }
    }

    private void AddV6Result(DiscoveredDevice discovered)
    {
        if (_resultsV6.Any(r => r.IpAddress == discovered.IpAddress)) return;
        bool known = _knownDevices.Any(d => d.Host == discovered.IpAddress);
        _resultsV6.Add(new ScanResultViewModel(discovered, known));
    }

    private void Ipv6Clear_Click(object sender, RoutedEventArgs e)
    {
        _resultsV6.Clear();
        Ipv6StatusText.Text = T("Sc_Ipv6Intro");
    }

    private void ScanSelectAllV6_Changed(object sender, RoutedEventArgs e)
    {
        var value = ScanSelectAllV6.IsChecked == true;
        foreach (var r in _resultsV6.Where(r => r.CanSelect)) r.IsSelected = value;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _results.Clear();
        ScanStatusText.Text = T("Sc_Intro");
    }

    private void ScanSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var value = ScanSelectAll.IsChecked == true;
        foreach (var r in _results.Where(r => r.CanSelect)) r.IsSelected = value;
    }

    private void ResultGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultGrid.SelectedItem is ScanResultViewModel { HasSmb: true } vm)
            _ = vm.LoadSharesAsync();
    }

    /// <summary>Double-click on a scan-result cell copies its value to the clipboard.</summary>
    private void Result_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        TextBlock? tb = null;
        while (src is not null and not DataGridCell)
        {
            if (tb is null && src is TextBlock t) tb = t;
            src = VisualTreeHelper.GetParent(src);
        }
        if (src is not DataGridCell cell) return;
        tb ??= cell.Content as TextBlock;
        if (tb is null || tb.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(tb.Text);
            var status = ReferenceEquals(sender, ResultGridV6) ? Ipv6StatusText : ScanStatusText;
            status.Text = T("Msg_Copied");
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }

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

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RunMndpAsync();

    private void Window_Closing(object sender, CancelEventArgs e) => _cts?.Cancel();

    private async void Mndp_Click(object sender, RoutedEventArgs e) => await RunMndpAsync();

    private async Task RunMndpAsync()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        SetScanUi(running: true, T("Sc_MndpRunning"));
        ScanProgress.IsIndeterminate = true;

        try
        {
            var found = await MndpScanner.DiscoverAsync(
                TimeSpan.FromSeconds(5),
                new Progress<DiscoveredDevice>(AddResult),
                _cts.Token);
            SetScanUi(running: false, T("Sc_MndpDone", found.Count));
        }
        catch (SocketException ex)
        {
            SetScanUi(running: false, T("Sc_MndpBusy", ex.Message));
        }
        catch (Exception ex)
        {
            SetScanUi(running: false, T("Sc_MndpError", ex.Message));
        }
        finally
        {
            ScanProgress.IsIndeterminate = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async void ScanSubnet_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        int totalHosts;
        try { totalHosts = SubnetScanner.CountHosts(SubnetBox.Text); }
        catch (ArgumentException ex)
        {
            ScanStatusText.Text = ex.Message;
            return;
        }

        _cts = new CancellationTokenSource();
        SetScanUi(running: true, T("Sc_Scanning", SubnetBox.Text, totalHosts));
        ScanProgress.Maximum = totalHosts;
        ScanProgress.Value = 0;

        int scanned = 0, found = 0;
        try
        {
            await SubnetScanner.ScanAsync(
                SubnetBox.Text,
                new Progress<DiscoveredDevice>(d => { found++; AddResult(d); }),
                new Progress<int>(_ =>
                {
                    scanned++;
                    ScanProgress.Value = scanned;
                    ScanStatusText.Text = T("Sc_ScanProgress", SubnetBox.Text, scanned, totalHosts, found);
                }),
                _cts.Token);
            SetScanUi(running: false, T("Sc_ScanDone", found, SubnetBox.Text));
        }
        catch (OperationCanceledException)
        {
            SetScanUi(running: false, T("Sc_ScanCancelled"));
        }
        catch (Exception ex)
        {
            SetScanUi(running: false, T("Sc_ScanError", ex.Message));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetScanUi(bool running, string status)
    {
        MndpButton.IsEnabled = !running;
        ScanButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ScanProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ScanStatusText.Text = status;
    }

    private void AddResult(DiscoveredDevice discovered)
    {
        // Merge duplicates between MNDP and scan (and repeated scans)
        var existing = _results.FirstOrDefault(r =>
            r.IpAddress == discovered.IpAddress ||
            (discovered.MacAddress != "" && r.MacAddress == discovered.MacAddress));
        if (existing is not null)
        {
            if (existing.Source == "Scan" && discovered.Source == "MNDP")
            {
                _results.Remove(existing); // the MNDP result is more informative
            }
            else
            {
                return;
            }
        }

        bool known = _knownDevices.Any(d =>
            d.Host == discovered.IpAddress ||
            (discovered.MacAddress != "" && string.Equals(d.MacAddress, discovered.MacAddress, StringComparison.OrdinalIgnoreCase)));
        _results.Add(new ScanResultViewModel(discovered, known));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var selected = _results.Concat(_resultsV6).Where(r => r.IsSelected && !r.AlreadyAdded).ToList();
        if (selected.Count == 0)
        {
            ScanStatusText.Text = T("Sc_NoneSelected");
            return;
        }

        foreach (var result in selected)
        {
            NewDevices.Add(new Device
            {
                Name = result.Identity.Length > 0 ? result.Identity : result.IpAddress,
                Host = result.IpAddress,
                MacAddress = result.MacAddress,
                Username = _defaultUsername,
                EncryptedPassword = _defaultEncryptedPassword,
                UseHttps = true,
                Port = 443,
                IgnoreCertErrors = true,
            });
        }
        DialogResult = true;
    }

    /// <summary>Returns the /24 of the primary local IPv4 address as a suggestion.</summary>
    private static string GuessLocalSubnet()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
            }
        }
        return "192.168.1.0/24";
    }
}
