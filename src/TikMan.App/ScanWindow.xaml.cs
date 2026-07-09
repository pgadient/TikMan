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
    public bool HasSmb => Discovered.OpenPorts.Contains(445) || Discovered.OpenPorts.Contains(139);
    public string Board => Discovered.Board;
    public string Version => Discovered.Version;
    public string Source => Discovered.Source;
    public bool CanSelect => !AlreadyAdded;

    public string Hint
    {
        get
        {
            var parts = new List<string>();
            if (AlreadyAdded) parts.Add(T("Sc_HintAlreadyAdded"));
            else if (Discovered.IsLikelyMikroTik && Discovered.Source == "Scan") parts.Add(T("Sc_HintLikely"));
            if (IsGateway) parts.Add(T("Sc_GatewayModel", GatewayModel.Length > 0 ? GatewayModel : T("Val_Na")));
            else if (GatewayModel.Length > 0) parts.Add(GatewayModel);
            if (WebServer.Length > 0) parts.Add(T("Sc_HintWebServer", WebServer));
            return string.Join(" · ", parts);
        }
    }

    private bool _isGateway;
    /// <summary>True when this host is the local default gateway (its model is then highlighted).</summary>
    public bool IsGateway { get => _isGateway; set { if (_isGateway != value) { _isGateway = value; Notify(nameof(Hint)); } } }

    private string _webServer = "";
    /// <summary>HTTP Server header (nginx, IIS, …); filled by the async fingerprint probe.</summary>
    public string WebServer { get => _webServer; private set { _webServer = value; Notify(nameof(Hint)); } }

    private string _gatewayModel = "";
    /// <summary>Gateway/router product name pulled from the web UI title, or "".</summary>
    public string GatewayModel { get => _gatewayModel; private set { _gatewayModel = value; Notify(nameof(Hint)); } }

    private bool _httpProbed;

    /// <summary>Fingerprints the web interface once (Server header + gateway model) for web hosts.</summary>
    public async Task ProbeHttpAsync()
    {
        if (_httpProbed) return;
        if (!Discovered.OpenPorts.Contains(80) && !Discovered.OpenPorts.Contains(443)) return;
        _httpProbed = true;
        var info = await HttpFingerprint.ProbeAsync(IpAddress);
        WebServer = info.WebServer;
        GatewayModel = info.Model;
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

    /// <summary>Enumerates the host's SMB shares once, on first expand. NetShareEnum is a blocking
    /// native call, so it's raced against a timeout to keep the UI from hanging on a slow server.</summary>
    public async Task LoadSharesAsync()
    {
        if (!HasSmb || _sharesLoaded) return;
        _sharesLoaded = true;
        SharesStatus = T("Sc_SmbLoading");
        try
        {
            var listTask = SmbShares.ListAsync(IpAddress);
            if (await Task.WhenAny(listTask, Task.Delay(TimeSpan.FromSeconds(8))) != listTask)
            {
                _sharesLoaded = false; // let a re-expand try again
                SharesStatus = T("Sc_SmbTimeout");
                return;
            }

            var result = await listTask;
            foreach (var name in result.Shares) Shares.Add(new SmbShareVm(IpAddress, name));
            SharesStatus = result.Status switch
            {
                ShareListStatus.AccessDenied => T("Sc_SmbDenied"),
                _ when result.Shares.Count > 0 => "",
                _ => T("Sc_SmbNone"),
            };
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
    private readonly bool _defaultIgnoreCert;
    private readonly ObservableCollection<ScanResultViewModel> _results = new();
    private readonly ObservableCollection<ScanResultViewModel> _resultsV6 = new();
    private readonly HashSet<string> _gateways = NetworkInfo.GetDefaultGateways();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _ipv6Cts;
    private bool _ipv6Started;

    /// <summary>After DialogResult == true: the devices to be created.</summary>
    public List<Device> NewDevices { get; } = new();

    public ScanWindow(List<Device> knownDevices, string defaultUsername, string defaultEncryptedPassword,
        bool defaultIgnoreCert = true)
    {
        InitializeComponent();
        _knownDevices = knownDevices;
        _defaultUsername = defaultUsername;
        _defaultEncryptedPassword = defaultEncryptedPassword;
        _defaultIgnoreCert = defaultIgnoreCert;
        ResultGrid.ItemsSource = _results;
        ResultGridV6.ItemsSource = _resultsV6;
        SubnetBox.Text = GuessLocalSubnet();
    }

    // ----- IPv6 -----

    private void Ipv6Discover_Click(object sender, RoutedEventArgs e) => _ = StartIpv6DiscoveryAsync();

    /// <summary>Starts continuous IPv6 discovery when the IPv6 tab is first shown (the ND cache fills
    /// gradually, so a one-shot pass misses hosts). Inner ComboBox/DataGrid selection changes bubble
    /// here too, hence the source check.</summary>
    private void ScanTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return;
        if (ScanTabs.SelectedIndex == 1 && !_ipv6Started) _ = StartIpv6DiscoveryAsync();
    }

    private async Task StartIpv6DiscoveryAsync()
    {
        _ipv6Started = true;
        _ipv6Cts?.Cancel();
        _ipv6Cts = new CancellationTokenSource();
        var ct = _ipv6Cts.Token;

        Ipv6Button.IsEnabled = false;
        Ipv6Progress.Visibility = Visibility.Visible;
        Ipv6StatusText.Text = T("Sc_Ipv6Running");
        try
        {
            var found = await Ipv6Discovery.DiscoverContinuousAsync(
                TimeSpan.FromSeconds(15), new Progress<DiscoveredDevice>(AddV6Result), ct);
            Ipv6StatusText.Text = T("Sc_Ipv6Done", found);
        }
        catch (OperationCanceledException) { /* restarted or window closing */ }
        catch (Exception ex) { Ipv6StatusText.Text = T("Sc_Ipv6Error", ex.Message); }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                Ipv6Progress.Visibility = Visibility.Collapsed;
                Ipv6Button.IsEnabled = true;
            }
        }
    }

    private void AddV6Result(DiscoveredDevice discovered)
    {
        if (_resultsV6.Any(r => r.IpAddress == discovered.IpAddress)) return;
        bool known = _knownDevices.Any(d => d.Host == discovered.IpAddress);
        var vm = new ScanResultViewModel(discovered, known)
        {
            IsGateway = _gateways.Contains(discovered.IpAddress),
            IsSelected = !known && ScanSelectAllV6.IsChecked == true, // follow the header's state
        };
        _resultsV6.Add(vm);
        _ = vm.ProbeHttpAsync();
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

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _cts?.Cancel();
        _ipv6Cts?.Cancel();
    }

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
        var vm = new ScanResultViewModel(discovered, known)
        {
            IsGateway = _gateways.Contains(discovered.IpAddress),
            IsSelected = !known && ScanSelectAll.IsChecked == true, // follow the header's state
        };
        _results.Add(vm);
        _ = vm.ProbeHttpAsync();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var selected = _results.Concat(_resultsV6).Where(r => r.IsSelected && !r.AlreadyAdded).ToList();
        if (selected.Count == 0)
        {
            ScanStatusText.Text = T("Sc_NoneSelected");
            return;
        }

        foreach (var group in GroupByDevice(selected))
        {
            // Prefer an IPv4 address as the primary (REST over HTTPS); the other family becomes the alt.
            var primary = group.FirstOrDefault(r => IsIpv4(r.IpAddress)) ?? group[0];
            var alt = group.FirstOrDefault(r => !ReferenceEquals(r, primary));
            NewDevices.Add(new Device
            {
                Name = primary.Identity.Length > 0 ? primary.Identity : primary.IpAddress,
                Host = primary.IpAddress,
                AltAddress = alt?.IpAddress ?? "",
                MacAddress = primary.MacAddress.Length > 0 ? primary.MacAddress : (alt?.MacAddress ?? ""),
                Username = _defaultUsername,
                EncryptedPassword = _defaultEncryptedPassword,
                UseHttps = true,
                Port = 443,
                IgnoreCertErrors = _defaultIgnoreCert,
            });
        }
        DialogResult = true;
    }

    /// <summary>Groups the selected results so IPv4 and IPv6 of the same physical device (same MAC)
    /// become one device; entries without a MAC stay on their own.</summary>
    private static List<List<ScanResultViewModel>> GroupByDevice(IEnumerable<ScanResultViewModel> selected)
    {
        var groups = new List<List<ScanResultViewModel>>();
        var byMac = new Dictionary<string, List<ScanResultViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in selected)
        {
            var mac = new string(r.MacAddress.Where(Uri.IsHexDigit).ToArray());
            if (mac.Length == 0) { groups.Add(new List<ScanResultViewModel> { r }); continue; }
            if (!byMac.TryGetValue(mac, out var g)) { g = new List<ScanResultViewModel>(); byMac[mac] = g; groups.Add(g); }
            g.Add(r);
        }
        return groups;
    }

    private static bool IsIpv4(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>Suggests the local network as CIDR, using the interface's real subnet mask
    /// (prefix length) — e.g. 192.168.8.0/22 for a /22 LAN, not a hard-coded /24.</summary>
    private static string GuessLocalSubnet()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                int prefix = ua.PrefixLength;
                if (prefix is < 16 or > 32) prefix = 24; // keep the scan within a sane size
                return $"{NetworkAddress(ua.Address, prefix)}/{prefix}";
            }
        }
        return "192.168.1.0/24";
    }

    /// <summary>Zeroes the host bits of an IPv4 address for the given prefix, giving the network address.</summary>
    private static string NetworkAddress(IPAddress ip, int prefix)
    {
        var b = ip.GetAddressBytes();
        uint addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        uint net = addr & mask;
        return $"{(net >> 24) & 0xFF}.{(net >> 16) & 0xFF}.{(net >> 8) & 0xFF}.{net & 0xFF}";
    }
}
