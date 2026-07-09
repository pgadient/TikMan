using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
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
    public string Board => Discovered.Board;
    public string Version => Discovered.Version;
    public string Source => Discovered.Source;
    public bool CanSelect => !AlreadyAdded;

    public string Hint
    {
        get
        {
            if (AlreadyAdded) return T("Sc_HintAlreadyAdded");
            var parts = new List<string>();
            if (Discovered.IsLikelyMikroTik && Discovered.Source == "Scan") parts.Add(T("Sc_HintLikely"));
            if (Discovered.OpenPorts.Count > 0) parts.Add(T("Sc_HintPorts", string.Join(", ", Discovered.OpenPorts)));
            return string.Join(" – ", parts);
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class ScanWindow : Window
{
    private readonly List<Device> _knownDevices;
    private readonly string _defaultUsername;
    private readonly string _defaultEncryptedPassword;
    private readonly ObservableCollection<ScanResultViewModel> _results = new();
    private CancellationTokenSource? _cts;

    /// <summary>After DialogResult == true: the devices to be created.</summary>
    public List<Device> NewDevices { get; } = new();

    public ScanWindow(List<Device> knownDevices, string defaultUsername, string defaultEncryptedPassword)
    {
        InitializeComponent();
        _knownDevices = knownDevices;
        _defaultUsername = defaultUsername;
        _defaultEncryptedPassword = defaultEncryptedPassword;
        ResultGrid.ItemsSource = _results;
        SubnetBox.Text = GuessLocalSubnet();
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
        var selected = _results.Where(r => r.IsSelected && !r.AlreadyAdded).ToList();
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
