using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>Network discovery moved into the main view: MNDP + IPv4 subnet + IPv6 run together and
/// every found host is added to the list automatically (no separate scan dialog).</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _scanCts;
    private bool _scanning;
    /// <summary>IPv4 targets of a manual scan – MNDP hits outside this set are then filtered out
    /// (so a manual scan of one subnet doesn't list MikroTik devices from another). Null = no filter.</summary>
    private HashSet<string>? _scanTargets;
    private List<LocalSubnet> _subnets = new();
    private int _subnetIndex;
    private const int AutoScanMaxHosts = 8192; // don't ping-sweep a huge subnet automatically

    private const double Ipv6DurationSeconds = 15; // IPv6 discovery window (matches the bar's max)
    private readonly DispatcherTimer _ipv6ProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double _ipv6Elapsed;

    private const double MndpDurationSeconds = 5; // MNDP listen window (matches the bar's max)
    private readonly DispatcherTimer _mndpProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double _mndpElapsed;

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

    /// <summary>Enter in the subnet box starts the scan; Escape clears the box.</summary>
    private async void SubnetBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await RunDiscoveryAsync(auto: false); }
        else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; SubnetBox.Clear(); }
    }

    /// <summary>Runs MNDP + IPv4 subnet + IPv6 discovery together and auto-adds every found host.
    /// Clicking the button again while running stops it.</summary>
    /// <summary>View toggle: keep re-running discovery back-to-back. Starts a pass if none is running;
    /// each pass chains the next while the box stays checked (see the tail of RunDiscoveryAsync).</summary>
    private void ContinuousScan_Changed(object sender, RoutedEventArgs e)
    {
        if (ContinuousScanCheck.IsChecked == true && !_scanning) _ = RunDiscoveryAsync(auto: true);
    }

    private async Task RunDiscoveryAsync(bool auto)
    {
        // Clicking the Stop button also ends continuous mode, so a stop really stops.
        if (_scanning) { _scanCts?.Cancel(); ContinuousScanCheck.IsChecked = false; return; }
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        ScanButton.Content = T("Sc_Stop");
        DiscoveryProgressPanel.Visibility = Visibility.Visible;
        Ipv4ProgressRow.Visibility = Visibility.Visible;
        MndpProgressRow.Visibility = Visibility.Visible;
        ZonProgressRow.Visibility = ZdpScanner.IsAvailable() ? Visibility.Visible : Visibility.Collapsed; // only with Npcap
        Ipv4MetaProgressRow.Visibility = Visibility.Visible;
        Ipv6ProgressRow.Visibility = Visibility.Visible;
        Ipv6MetaProgressRow.Visibility = Visibility.Visible;
        Ipv4Progress.IsIndeterminate = false;
        Ipv4Progress.Value = 0;
        Ipv4MetaProgress.Value = 0;
        Ipv6MetaProgress.Value = 0;
        StartMndpProgressTimer();
        StartIpv6ProgressTimer();
        SetStatus(T("Msg_Discovering"));

        var found = new Progress<DiscoveredDevice>(AddDiscovered);
        try
        {
            var mndp = MndpScanner.DiscoverAsync(TimeSpan.FromSeconds(5), found, ct);
            var ipv6 = Ipv6Discovery.DiscoverContinuousAsync(TimeSpan.FromSeconds(Ipv6DurationSeconds), found, ct);
            // Zyxel ZON/ZDP: finds Zyxel switches/APs on the local L2 with model + firmware (needs Npcap).
            var zon = ZdpScanner.DiscoverAsync(TimeSpan.FromSeconds(5), found, ct);

            Task subnet = Task.CompletedTask;
            var target = SubnetBox.Text.Trim();
            // A manual scan of a specific subnet only lists devices in that range – MNDP would
            // otherwise add MikroTik devices from anywhere on the L2 segment.
            _scanTargets = null;
            if (!auto && target.Length > 0)
            {
                try { _scanTargets = SubnetScanner.EnumerateTargets(target).Select(ip => ip.ToString()).ToHashSet(); }
                catch (ArgumentException) { /* invalid target – no filter */ }
            }
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
                        subnet = SubnetScanner.ScanAsync(target, found, onScanned, ct,
                            _appData.PingTimeoutMs, _appData.PingRetries);
                    }
                }
                catch (ArgumentException) { Ipv4Progress.IsIndeterminate = true; } // invalid subnet – MNDP only
            }
            else
            {
                Ipv4Progress.IsIndeterminate = true; // no subnet target – just MNDP on the IPv4 side
            }

            // Hide each bar as soon as its own work finishes, so none sits at 100 % waiting.
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task gm = Guard(mndp), gi = Guard(ipv6), gs = Guard(subnet), gz = Guard(zon);
            _ = gm.ContinueWith(_ =>
                {
                    _mndpProgressTimer.Stop();
                    MndpProgressRow.Visibility = Visibility.Collapsed;
                }, CancellationToken.None, TaskContinuationOptions.None, ui);
            _ = gs.ContinueWith(_ => Ipv4ProgressRow.Visibility = Visibility.Collapsed,
                CancellationToken.None, TaskContinuationOptions.None, ui);
            _ = gi.ContinueWith(_ => Ipv6ProgressRow.Visibility = Visibility.Collapsed,
                CancellationToken.None, TaskContinuationOptions.None, ui);
            _ = gz.ContinueWith(_ => ZonProgressRow.Visibility = Visibility.Collapsed,
                CancellationToken.None, TaskContinuationOptions.None, ui);

            // Scans first, then the meta phase per address family – a stable base before analysis.
            async Task V4ChainAsync()
            {
                await Task.WhenAll(gm, gs, gz);
                await RunMetaPhaseAsync(v6: false, ct);
            }
            async Task V6ChainAsync()
            {
                await gi;
                await RunMetaPhaseAsync(v6: true, ct);
            }
            await Task.WhenAll(V4ChainAsync(), V6ChainAsync());
            SetStatus(T("Msg_DiscoveryDone", _devices.Count));
        }
        catch (OperationCanceledException) { SetStatus(T("Sc_ScanCancelled")); }
        catch (Exception ex) { SetStatus(T("Sc_ScanError", ex.Message)); }
        finally
        {
            _scanning = false;
            _scanTargets = null;
            ScanButton.Content = T("Tb_Scan");
            _ipv6ProgressTimer.Stop();
            _mndpProgressTimer.Stop();
            Ipv4Progress.IsIndeterminate = false;
            DiscoveryProgressPanel.Visibility = Visibility.Collapsed;
            MarkGateways();
            ApplyDeviceFilter(); // re-evaluate tab membership after v4/v6 merges
            UpdateDeviceCount();
            SaveAppData();
        }

        // Continuous mode: chain another pass unless it was switched off or the scan was stopped.
        if (ContinuousScanCheck.IsChecked == true && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { return; }
            if (ContinuousScanCheck.IsChecked == true && !_scanning) _ = RunDiscoveryAsync(auto: true);
        }
    }

    /// <summary>Meta phase after a scan: collects details (web fingerprint, WMI) about the devices
    /// of one address family, with its own progress bar. The IPv4 phase covers every device with an
    /// IPv4; the IPv6 phase covers the v6-only rest, so nothing is probed twice.</summary>
    private async Task RunMetaPhaseAsync(bool v6, CancellationToken ct)
    {
        var row = v6 ? Ipv6MetaProgressRow : Ipv4MetaProgressRow;
        var bar = v6 ? Ipv6MetaProgress : Ipv4MetaProgress;
        var targets = _devices.Where(d => v6 ? d.HasIpv6 && !d.HasIpv4 : d.HasIpv4).ToList();
        if (targets.Count == 0 || ct.IsCancellationRequested)
        {
            row.Visibility = Visibility.Collapsed;
            return;
        }

        bar.Maximum = targets.Count;
        bar.Value = 0;
        int done = 0;
        await Task.WhenAll(targets.Select(async vm =>
        {
            if (!ct.IsCancellationRequested)
                await EnrichDetailsAsync(vm, ct);
            bar.Value = ++done; // continuations resume on the UI thread
        }));
        row.Visibility = Visibility.Collapsed;
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

    /// <summary>Advances the MNDP bar from 0 to the listen window (5 s) while it runs.</summary>
    private void StartMndpProgressTimer()
    {
        _mndpElapsed = 0;
        MndpProgress.Value = 0;
        _mndpProgressTimer.Tick -= MndpProgressTick;
        _mndpProgressTimer.Tick += MndpProgressTick;
        _mndpProgressTimer.Start();
    }

    private void MndpProgressTick(object? sender, EventArgs e)
    {
        _mndpElapsed += 0.25;
        MndpProgress.Value = Math.Min(_mndpElapsed, MndpDurationSeconds);
        if (_mndpElapsed >= MndpDurationSeconds) _mndpProgressTimer.Stop();
    }

    /// <summary>Adds a discovered host to the list (dedup by IP, or merges the other-family address
    /// of the same MAC), then starts monitoring it if it looks like a MikroTik.</summary>
    private void AddDiscovered(DiscoveredDevice d)
    {
        var byIp = _devices.FirstOrDefault(v => v.HasAddress(d.IpAddress));
        if (byIp is not null) { EnrichExisting(byIp, d); ApplyDefaultExpansion(byIp); return; }

        // Manual subnet scan: ignore IPv4 hits (e.g. from MNDP broadcast) outside the entered range.
        if (_scanTargets is not null && !d.IpAddress.Contains(':') && !_scanTargets.Contains(d.IpAddress))
            return;

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

        // SwOS announces itself via MNDP with a 2.x version (RouterOS is 6.x/7.x). It has no
        // REST/SSH API, so monitoring would only produce errors – ping status covers it.
        bool swos = d.Source == "MNDP" && d.Version.TrimStart().StartsWith("2.");
        bool likely = (d.IsLikelyMikroTik || d.Source == "MNDP") && !swos;
        var device = new Device
        {
            // No hostname learned → leave the name empty (an IP address as name reads poorly).
            Name = d.Identity,
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
        var vm = new DeviceViewModel(device);
        vm.ApplyDiscoveryFacts(d.Board, d.Version, swos); // MNDP already names board + version (SwOS!)
        _devices.Add(vm);
        ApplyDefaultExpansion(vm);
        if (likely) _ = RefreshAndCheckAsync(vm);
        else vm.MarkOnline(); // just found by discovery ⇒ reachable (green)
        // Details (web fingerprint, WMI) are collected in the meta phase after the scans finish.
    }

    /// <summary>Best-effort background enrichment for the Details tab: the web server (Server header +
    /// page title) for web hosts, and WMI facts (manufacturer/model/OS) for Windows hosts (port 135).</summary>
    private async Task EnrichDetailsAsync(DeviceViewModel vm, CancellationToken ct = default)
    {
        var ports = vm.Model.OpenPorts;
        var info = vm.Model.ExtraInfo;
        bool changed = false;

        // A web UI is worth probing when a web port is open OR when we simply don't know the ports
        // yet (manually added device, or a host that only answered ping) – identifying the vendor is
        // what lets the per-vendor probes below run, especially over VPN where there is no MAC.
        bool webMaybe = ports.Count == 0 || ports.Contains(80) || ports.Contains(443);
        bool webOrAlt = webMaybe || ports.Contains(8080);

        // QNAP NAS: the GUI is usually on 8080 and returns HTTP errors on plain paths – ask its
        // QTS login endpoint instead so we never mistake a "403 Forbidden" page for a model.
        if (webOrAlt && !ct.IsCancellationRequested)
        {
            try
            {
                var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
                if (await QnapProbe.QueryAsync(host, ports, ct) is { } qnap)
                {
                    vm.ApplyQnapInfo(qnap);
                    return; // QNAP identified – its plain web pages would only add noise
                }
            }
            catch { /* best effort */ }
        }

        if (webMaybe && !ct.IsCancellationRequested)
        {
            try
            {
                var fp = await HttpFingerprint.ProbeAsync(vm.Host, ct);
                if (fp.WebServer.Length > 0) { info["Webserver"] = fp.WebServer; changed = true; }
                if (fp.Vendor.Length > 0 && !info.ContainsKey("Hersteller (Web)")) { info["Hersteller (Web)"] = fp.Vendor; changed = true; }
                if (fp.Title.Length > 0) { info["Web-Titel"] = fp.Title; changed = true; }
            }
            catch { /* best effort */ }
        }

        // TLS certificate (port 443): some devices embed model/serial/MAC in the cert subject and
        // speak only legacy TLS the HTTP client can't fetch a page over (e.g. a Cisco SPA122 ATA).
        // Runs after the web fingerprint so a clean web-title model keeps precedence over the cert.
        if ((ports.Contains(443) || ports.Count == 0) && !ct.IsCancellationRequested)
        {
            try
            {
                var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
                if (await TlsCertProbe.QueryAsync(host, 443, ct) is { } tls)
                {
                    vm.ApplyTlsCertInfo(tls);
                    changed = false; // ApplyTlsCertInfo already raised the details
                }
            }
            catch { /* best effort */ }
        }

        if (ports.Contains(135) && OperatingSystem.IsWindows() && !ct.IsCancellationRequested)
        {
            try
            {
                foreach (var kv in await WmiProbe.QueryAsync(vm.Host, ct)) { info[kv.Key] = kv.Value; changed = true; }
                // The BIOS serial lives in the serial-number column, not in the details rows.
                if (info.TryGetValue("Seriennummer", out var sn))
                {
                    info.Remove("Seriennummer");
                    if (vm.Model.SerialNumber.Length == 0) { vm.Model.SerialNumber = sn; changed = true; }
                }
            }
            catch { /* best effort */ }
        }

        // Brother printers expose serial + main/sub firmware on an unauthenticated EWS page.
        if (webMaybe && LooksLikeBrother(vm) && !ct.IsCancellationRequested)
        {
            try
            {
                var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
                if (await BrotherProbe.QueryAsync(host, ct) is { } brother)
                {
                    vm.ApplyBrotherInfo(brother);
                    changed = false; // ApplyBrotherInfo already raised the details
                }
            }
            catch { /* best effort */ }
        }

        // Swisscom Internet-Boxes name their exact model over the SoftAtHome API / login page.
        if (webMaybe && LooksLikeInternetBox(vm) && !ct.IsCancellationRequested)
        {
            try
            {
                var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
                if (await SwisscomProbe.QueryAsync(host, ct) is { } box)
                {
                    vm.ApplySwisscomInfo(box);
                    changed = false; // ApplySwisscomInfo already raised the details
                }
            }
            catch { /* best effort */ }
        }

        // Generic SSH probe: reveals model/serial/firmware over read-only "show"/"print" commands,
        // ordered per vendor (MikroTik included – it serves as fallback when the REST API is off;
        // a filled board name means the RouterOS/TP-Link connector already delivered).
        if (ports.Contains(22) && vm.Board.Length == 0 && !ct.IsCancellationRequested &&
            vm.Model.Username.Trim().Length > 0 && vm.Model.EncryptedPassword.Length > 0 &&
            (vm.SerialNumber.Length == 0 || vm.Version.Length == 0))
        {
            try
            {
                var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
                var password = CredentialProtector.Unprotect(vm.Model.EncryptedPassword);
                if (password.Length > 0 &&
                    await SshInfoProbe.QueryAsync(host, vm.Model.SshPort, vm.Model.Username.Trim(),
                        password, $"{vm.IdentifiedVendor} {vm.MacVendor}", ct) is { } sshInfo)
                {
                    vm.ApplySshInfo(sshInfo);
                    changed = false; // ApplySshInfo already raised the details
                }
            }
            catch { /* best effort */ }
        }

        // Frontier-Silicon internet radios (Teufel, Hama, …) name themselves on GET /device.
        if (webMaybe && LooksLikeFsRadio(vm) && !ct.IsCancellationRequested)
        {
            try
            {
                var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
                if (await FrontierSiliconProbe.QueryAsync(host, ct) is { } radio)
                {
                    vm.ApplyRadioInfo(radio);
                    changed = false; // ApplyRadioInfo already raised the details
                }
            }
            catch { /* best effort */ }
        }

        // SNMP (public community) on every recognised IP: a responding host gets an "snmp" badge
        // like the other protocols. In addition – only when the richer probes above left the model/
        // vendor/OS blank – the exact model from sysDescr fills those gaps, unauthenticated.
        if (!ct.IsCancellationRequested)
        {
            try
            {
                var host = vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host;
                if (await SnmpProbe.QueryAsync(host, ct) is { } snmp)
                {
                    vm.MarkSnmpOpen(); // badge (also raises SupportedProtocols)
                    if (vm.Board.Length == 0 &&
                        (vm.ModelDisplay.Length == 0 || vm.IdentifiedVendor.Length == 0 || vm.OsDisplay.Length == 0))
                    {
                        vm.ApplySnmpInfo(snmp);
                        changed = false; // ApplySnmpInfo already raised the details
                    }
                }
            }
            catch { /* best effort */ }
        }

        // DNS (UDP 53) on every recognised IP: a UDP-only forwarder (Swisscom Internet-Box, dnsmasq,
        // …) isn't seen by the TCP scan, so probe it directly and add a "dns" badge when it answers.
        if (!ct.IsCancellationRequested)
        {
            try { if (await DnsProbe.IsOpenAsync(vm.Ipv4Address.Length > 0 ? vm.Ipv4Address : vm.Host, ct)) vm.MarkDnsOpen(); }
            catch { /* best effort */ }
        }

        // IPv6 via EUI-64: a known device that derives its link-local from the MAC (MikroTik & most
        // embedded gear) is confirmed online over IPv6 even when it ignored the passive ff02::1 poke.
        if (!vm.HasIpv6 && vm.Model.MacAddress.Length > 0 && OperatingSystem.IsWindows() && !ct.IsCancellationRequested)
        {
            try
            {
                if (await Ipv6LinkLocal.SolicitAsync(vm.Model.MacAddress, ct) is { } ll && !vm.HasAddress(ll))
                {
                    vm.Model.AltAddresses.Add(ll);
                    vm.RefreshAddressDisplay();
                    changed = true;
                }
            }
            catch { /* best effort */ }
        }

        if (changed) vm.RaiseDetailsChanged();
    }

    /// <summary>Every vendor signal we hold for a device, merged into one lowercase haystack: the OUI
    /// MAC vendor, the identified (web/WMI/model-derived) vendor, the model and the scraped web title.
    /// The per-vendor probes match against this so they fire off the manufacturer even when there is
    /// no MAC (e.g. VPN scans, where the vendor comes from the web UI, not the OUI).</summary>
    private static string VendorHint(DeviceViewModel vm)
    {
        var title = vm.Model.ExtraInfo.TryGetValue("Web-Titel", out var t) ? t : "";
        return $"{vm.MacVendor} {vm.IdentifiedVendor} {vm.ModelDisplay} {title}".ToLowerInvariant();
    }

    private static bool LooksLikeFsRadio(DeviceViewModel vm) =>
        VendorHint(vm).Contains("frontier") ||
        (vm.Model.ExtraInfo.TryGetValue("Web-Titel", out var t) &&
         t.StartsWith("Internet Radio", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeInternetBox(DeviceViewModel vm) =>
        VendorHint(vm).Contains("swisscom") ||
        VendorHint(vm).Replace("-", "").Replace(" ", "").Contains("internetbox");

    private static bool LooksLikeBrother(DeviceViewModel vm) =>
        VendorHint(vm).Contains("brother");

    /// <summary>Fills in facts a later discovery source learned about a device already in the list.</summary>
    private static void EnrichExisting(DeviceViewModel vm, DiscoveredDevice d)
    {
        bool changed = false;
        if (vm.Model.MacAddress.Length == 0 && d.MacAddress.Length > 0) { vm.Model.MacAddress = d.MacAddress; changed = true; }
        if (vm.Model.OpenPorts.Count == 0 && d.OpenPorts.Count > 0) { vm.Model.OpenPorts = d.OpenPorts; changed = true; }
        if (!vm.Model.HasSmb && (d.OpenPorts.Contains(445) || d.OpenPorts.Contains(139))) { vm.Model.HasSmb = true; changed = true; }
        vm.ApplyDiscoveryFacts(d.Board, d.Version,
            swos: d.Source == "MNDP" && d.Version.TrimStart().StartsWith("2."));
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
