using System.Net;
using System.Net.Sockets;
using System.Windows;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>Network discovery moved into the main view: MNDP + IPv4 subnet + IPv6 run together and
/// every found host is added to the list automatically (no separate scan dialog).</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _scanCts;
    private bool _scanning;
    private List<LocalSubnet> _subnets = new();
    private int _subnetIndex;
    private const int AutoScanMaxHosts = 8192; // don't ping-sweep a huge subnet automatically

    /// <summary>Fills the subnet box + source label from the local adapters (first one selected).</summary>
    private void InitSubnets()
    {
        _subnets = NetworkInfo.GetLocalSubnets();
        _subnetIndex = 0;
        ApplySubnetSelection();
    }

    private void ApplySubnetSelection()
    {
        if (_subnets.Count == 0)
        {
            if (SubnetBox.Text.Trim().Length == 0) SubnetBox.Text = "192.168.1.0/24";
            SubnetSourceText.Text = "";
            return;
        }
        var s = _subnets[_subnetIndex % _subnets.Count];
        SubnetBox.Text = s.Cidr;
        SubnetSourceText.Text = T("Sc_SubnetSource", s.Adapter, s.HostAddress);
    }

    private void CycleAdapter_Click(object sender, RoutedEventArgs e)
    {
        if (_subnets.Count == 0) { InitSubnets(); return; }
        _subnetIndex = (_subnetIndex + 1) % _subnets.Count;
        ApplySubnetSelection();
    }

    private async void ScanNow_Click(object sender, RoutedEventArgs e) => await RunDiscoveryAsync(auto: false);

    /// <summary>Runs MNDP + IPv4 subnet + IPv6 discovery together and auto-adds every found host.
    /// Clicking the button again while running stops it.</summary>
    private async Task RunDiscoveryAsync(bool auto)
    {
        if (_scanning) { _scanCts?.Cancel(); return; }
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        ScanButton.Content = T("Sc_Stop");
        ScanProgress.IsIndeterminate = true;
        ScanProgress.Visibility = Visibility.Visible;
        SetStatus(T("Msg_Discovering"));

        var found = new Progress<DiscoveredDevice>(AddDiscovered);
        try
        {
            var mndp = MndpScanner.DiscoverAsync(TimeSpan.FromSeconds(5), found, ct);
            var ipv6 = Ipv6Discovery.DiscoverContinuousAsync(TimeSpan.FromSeconds(15), found, ct);

            Task subnet = Task.CompletedTask;
            var target = SubnetBox.Text.Trim();
            if (target.Length > 0)
            {
                try
                {
                    int hosts = SubnetScanner.CountHosts(target);
                    if (auto && hosts > AutoScanMaxHosts)
                        SetStatus(T("Msg_SubnetTooBigAuto", hosts)); // MNDP + IPv6 still run
                    else
                        subnet = SubnetScanner.ScanAsync(target, found, null, ct);
                }
                catch (ArgumentException) { /* invalid subnet spec – skip the subnet leg */ }
            }

            await Task.WhenAll(Guard(mndp), Guard(ipv6), Guard(subnet));
            SetStatus(T("Msg_DiscoveryDone", _devices.Count));
        }
        catch (OperationCanceledException) { SetStatus(T("Sc_ScanCancelled")); }
        catch (Exception ex) { SetStatus(T("Sc_ScanError", ex.Message)); }
        finally
        {
            _scanning = false;
            ScanButton.Content = T("Tb_Scan");
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanProgress.IsIndeterminate = false;
            MarkGateways();
            SaveAppData();
        }
    }

    /// <summary>Adds a discovered host to the list (dedup by IP, or merges the other-family address
    /// of the same MAC), then starts monitoring it if it looks like a MikroTik.</summary>
    private void AddDiscovered(DiscoveredDevice d)
    {
        var byIp = _devices.FirstOrDefault(v =>
            string.Equals(v.Host, d.IpAddress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.Model.AltAddress, d.IpAddress, StringComparison.OrdinalIgnoreCase));
        if (byIp is not null) { EnrichExisting(byIp, d); return; }

        // Same physical device (same MAC), other IP family → attach as the alternate address.
        if (d.MacAddress.Length > 0)
        {
            var byMac = _devices.FirstOrDefault(v => MacEquals(v.Model.MacAddress, d.MacAddress));
            if (byMac is not null && byMac.Model.AltAddress.Length == 0 && FamilyDiffers(byMac.Host, d.IpAddress))
            {
                byMac.Model.AltAddress = d.IpAddress;
                byMac.RefreshAddressDisplay();
                return;
            }
        }

        bool likely = d.IsLikelyMikroTik || d.Source == "MNDP";
        var device = new Device
        {
            Name = d.Identity.Length > 0 ? d.Identity : d.IpAddress,
            Host = d.IpAddress,
            MacAddress = d.MacAddress,
            OpenPorts = d.OpenPorts,
            HasSmb = d.OpenPorts.Contains(445) || d.OpenPorts.Contains(139),
            Username = _appData.DefaultUsername,
            EncryptedPassword = _appData.DefaultEncryptedPassword,
            UseHttps = true,
            Port = 443,
            IgnoreCertErrors = _appData.DefaultIgnoreCertErrors,
            MonitoringEnabled = likely,
        };
        var vm = new DeviceViewModel(device) { IsSelected = MainSelectAll.IsChecked == true };
        _devices.Add(vm);
        if (likely) _ = RefreshAndCheckAsync(vm);
    }

    /// <summary>Fills in facts a later discovery source learned about a device already in the list.</summary>
    private static void EnrichExisting(DeviceViewModel vm, DiscoveredDevice d)
    {
        bool changed = false;
        if (vm.Model.MacAddress.Length == 0 && d.MacAddress.Length > 0) { vm.Model.MacAddress = d.MacAddress; changed = true; }
        if (vm.Model.OpenPorts.Count == 0 && d.OpenPorts.Count > 0) { vm.Model.OpenPorts = d.OpenPorts; changed = true; }
        if (!vm.Model.HasSmb && (d.OpenPorts.Contains(445) || d.OpenPorts.Contains(139))) { vm.Model.HasSmb = true; changed = true; }
        if (changed) vm.RaiseDiscoveryChanged();
    }

    private static bool MacEquals(string a, string b)
    {
        var na = NormalizeMac(a);
        return na.Length > 0 && na == NormalizeMac(b);
    }

    private static string NormalizeMac(string mac) =>
        new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static bool IsIpv4(string ip) =>
        IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork;

    private static bool FamilyDiffers(string a, string b) => IsIpv4(a) != IsIpv4(b);

    /// <summary>Awaits one discovery leg but swallows its own failure (e.g. MNDP port busy) so the
    /// other legs still finish; cancellation still propagates to stop the whole run.</summary>
    private static async Task Guard(Task leg)
    {
        try { await leg.ConfigureAwait(true); }
        catch (OperationCanceledException) { throw; }
        catch { /* this discovery method failed – the others carry on */ }
    }
}
