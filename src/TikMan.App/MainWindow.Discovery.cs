using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
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

    private const double Ipv6DurationSeconds = 15; // IPv6 discovery window (matches the bar's max)
    private readonly DispatcherTimer _ipv6ProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double _ipv6Elapsed;

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
        DiscoveryProgressPanel.Visibility = Visibility.Visible;
        Ipv4ProgressRow.Visibility = Visibility.Visible;
        Ipv6ProgressRow.Visibility = Visibility.Visible;
        Ipv4Progress.IsIndeterminate = false;
        Ipv4Progress.Value = 0;
        StartIpv6ProgressTimer();
        SetStatus(T("Msg_Discovering"));

        var found = new Progress<DiscoveredDevice>(AddDiscovered);
        try
        {
            var mndp = MndpScanner.DiscoverAsync(TimeSpan.FromSeconds(5), found, ct);
            var ipv6 = Ipv6Discovery.DiscoverContinuousAsync(TimeSpan.FromSeconds(Ipv6DurationSeconds), found, ct);

            Task subnet = Task.CompletedTask;
            var target = SubnetBox.Text.Trim();
            if (target.Length > 0)
            {
                try
                {
                    int hosts = SubnetScanner.CountHosts(target);
                    if (auto && hosts > AutoScanMaxHosts)
                    {
                        SetStatus(T("Msg_SubnetTooBigAuto", hosts)); // MNDP + IPv6 still run
                        Ipv4Progress.IsIndeterminate = true;
                    }
                    else
                    {
                        Ipv4Progress.Maximum = hosts;
                        int scanned = 0;
                        var onScanned = new Progress<int>(_ =>
                        {
                            Ipv4Progress.Value = Math.Min(++scanned, hosts);
                            if (scanned >= hosts) Ipv4ProgressRow.Visibility = Visibility.Collapsed; // hide at 100 %
                        });
                        subnet = SubnetScanner.ScanAsync(target, found, onScanned, ct);
                    }
                }
                catch (ArgumentException) { Ipv4Progress.IsIndeterminate = true; } // invalid subnet – MNDP only
            }
            else
            {
                Ipv4Progress.IsIndeterminate = true; // no subnet target – just MNDP on the IPv4 side
            }

            // Hide each bar as soon as its own work finishes, so neither sits at 100 % waiting.
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task gm = Guard(mndp), gi = Guard(ipv6), gs = Guard(subnet);
            _ = Task.WhenAll(gm, gs).ContinueWith(_ => Ipv4ProgressRow.Visibility = Visibility.Collapsed,
                CancellationToken.None, TaskContinuationOptions.None, ui);
            _ = gi.ContinueWith(_ => Ipv6ProgressRow.Visibility = Visibility.Collapsed,
                CancellationToken.None, TaskContinuationOptions.None, ui);

            await Task.WhenAll(gm, gi, gs);
            SetStatus(T("Msg_DiscoveryDone", _devices.Count));
        }
        catch (OperationCanceledException) { SetStatus(T("Sc_ScanCancelled")); }
        catch (Exception ex) { SetStatus(T("Sc_ScanError", ex.Message)); }
        finally
        {
            _scanning = false;
            ScanButton.Content = T("Tb_Scan");
            _ipv6ProgressTimer.Stop();
            Ipv4Progress.IsIndeterminate = false;
            DiscoveryProgressPanel.Visibility = Visibility.Collapsed;
            MarkGateways();
            ApplyDeviceFilter(); // re-evaluate tab membership after v4/v6 merges
            SaveAppData();
        }
    }

    /// <summary>Advances the IPv6 bar from 0 to the discovery window (15 s) while it runs.</summary>
    private void StartIpv6ProgressTimer()
    {
        _ipv6Elapsed = 0;
        Ipv6Progress.Value = 0;
        _ipv6ProgressTimer.Tick -= Ipv6ProgressTick;
        _ipv6ProgressTimer.Tick += Ipv6ProgressTick;
        _ipv6ProgressTimer.Start();
    }

    private void Ipv6ProgressTick(object? sender, EventArgs e)
    {
        _ipv6Elapsed += 0.25;
        Ipv6Progress.Value = Math.Min(_ipv6Elapsed, Ipv6DurationSeconds);
        if (_ipv6Elapsed >= Ipv6DurationSeconds) _ipv6ProgressTimer.Stop();
    }

    /// <summary>Adds a discovered host to the list (dedup by IP, or merges the other-family address
    /// of the same MAC), then starts monitoring it if it looks like a MikroTik.</summary>
    private void AddDiscovered(DiscoveredDevice d)
    {
        var byIp = _devices.FirstOrDefault(v => v.HasAddress(d.IpAddress));
        if (byIp is not null) { EnrichExisting(byIp, d); ApplyDefaultExpansion(byIp); return; }

        // Same physical device (same MAC) → attach this address. A NIC often has several IPv6
        // addresses (global, ULA, link-local, privacy), so we collect them all, not just one.
        if (d.MacAddress.Length > 0)
        {
            var byMac = _devices.FirstOrDefault(v => MacEquals(v.Model.MacAddress, d.MacAddress));
            if (byMac is not null)
            {
                byMac.Model.AltAddresses.Add(d.IpAddress);
                byMac.RefreshAddressDisplay();
                EnrichExisting(byMac, d);
                ApplyDefaultExpansion(byMac);
                return;
            }
        }

        bool likely = d.IsLikelyMikroTik || d.Source == "MNDP";
        var device = new Device
        {
            // No hostname learned over IPv6 → leave the name empty (an IPv6 as name reads poorly).
            Name = d.Identity.Length > 0 ? d.Identity : d.IpAddress.Contains(':') ? "" : d.IpAddress,
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
        ApplyDefaultExpansion(vm);
        if (likely) _ = RefreshAndCheckAsync(vm);
        else vm.MarkOnline(); // just found by discovery ⇒ reachable (green)
        _ = EnrichDetailsAsync(vm);
    }

    /// <summary>Best-effort background enrichment for the Details tab: the web server (Server header +
    /// page title) for web hosts, and WMI facts (manufacturer/model/OS) for Windows hosts (port 135).</summary>
    private static async Task EnrichDetailsAsync(DeviceViewModel vm)
    {
        var ports = vm.Model.OpenPorts;
        var info = vm.Model.ExtraInfo;
        bool changed = false;

        if (ports.Contains(80) || ports.Contains(443))
        {
            try
            {
                var fp = await HttpFingerprint.ProbeAsync(vm.Host);
                if (fp.WebServer.Length > 0) { info["Webserver"] = fp.WebServer; changed = true; }
                if (fp.Vendor.Length > 0) { info["Hersteller (Web)"] = fp.Vendor; changed = true; }
                if (fp.Title.Length > 0) { info["Web-Titel"] = fp.Title; changed = true; }
            }
            catch { /* best effort */ }
        }

        if (ports.Contains(135) && OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var kv in await WmiProbe.QueryAsync(vm.Host)) { info[kv.Key] = kv.Value; changed = true; }
            }
            catch { /* best effort */ }
        }

        if (changed) vm.RaiseDetailsChanged();
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

    /// <summary>Awaits one discovery leg but swallows its own failure (e.g. MNDP port busy) so the
    /// other legs still finish; cancellation still propagates to stop the whole run.</summary>
    private static async Task Guard(Task leg)
    {
        try { await leg.ConfigureAwait(true); }
        catch (OperationCanceledException) { throw; }
        catch { /* this discovery method failed – the others carry on */ }
    }
}
