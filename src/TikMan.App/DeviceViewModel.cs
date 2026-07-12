using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TikMan.App.Localization;
using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

public enum DeviceStatus { Unknown, Online, Offline, Busy }

/// <summary>Bindable runtime state of a device. All methods must be called from the UI
/// thread (async/await returns to the UI context).</summary>
public class DeviceViewModel : INotifyPropertyChanged
{
    private const int MaxHistory = 240;
    private static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-CH");

    public Device Model { get; }
    private RouterOsClient? _client;

    public DeviceViewModel(Device model)
    {
        Model = model;
        RaiseDetailsChanged();
    }

    /// <summary>Extra discovery facts (WMI / web) shown as key/value rows in the Details tab.</summary>
    public ObservableCollection<InfoRow> ExtraInfo { get; } = new();
    public bool HasExtraInfo => ExtraInfo.Count > 0;

    /// <summary>Rebuilds the Details "extra info" rows from the model dictionary (after enrichment).</summary>
    public void RaiseDetailsChanged()
    {
        ExtraInfo.Clear();
        foreach (var kv in Model.ExtraInfo) ExtraInfo.Add(new InfoRow(kv.Key, kv.Value));
        Notify(nameof(HasExtraInfo));
        Notify(nameof(IdentifiedVendor)); // the web-scraped/WMI manufacturer may have just arrived
        Notify(nameof(ModelDisplay));     // …and WMI may have supplied a model
        Notify(nameof(DeviceType));       // …or a form factor (laptop/notebook/tablet)
        Notify(nameof(SerialNumber));
        Notify(nameof(FirmwareDetails));
        Notify(nameof(HasRowDetails));
    }

    /// <summary>Hardware serial number (RouterBOARD, Brother maintenance page, …).</summary>
    public string SerialNumber => Model.SerialNumber;

    /// <summary>Sub-firmware versions (e.g. Brother Sub1/Sub2/Sub4) for the expanded row.</summary>
    public IReadOnlyList<string> FirmwareDetails =>
        Model.ExtraInfo
            .Where(kv => kv.Key.StartsWith("Sub", StringComparison.OrdinalIgnoreCase) &&
                         kv.Key.Contains("Firmware", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

    /// <summary>Applies what the Brother maintenance page reported (serial, main/sub firmware).</summary>
    public void ApplyBrotherInfo(BrotherProbe.BrotherInfo info)
    {
        if (info.Serial.Length > 0) Model.SerialNumber = info.Serial;
        if (info.MainFirmware.Length > 0 && Version.Length == 0) Version = info.MainFirmware;
        foreach (var kv in info.SubFirmware) Model.ExtraInfo[kv.Key] = kv.Value;
        RaiseDetailsChanged();
    }

    /// <summary>Applies what a Frontier-Silicon radio (Teufel & Co.) reported.</summary>
    public void ApplyRadioInfo(FrontierSiliconProbe.RadioInfo radio)
    {
        if (radio.Name.Length > 0) Model.ExtraInfo["Modell"] = radio.Name; // feeds ModelDisplay
        if (radio.Vendor.Length > 0 && !Model.ExtraInfo.ContainsKey("Hersteller (Web)"))
            Model.ExtraInfo["Hersteller (Web)"] = radio.Vendor;
        if (radio.Serial.Length > 0 && Model.SerialNumber.Length == 0) Model.SerialNumber = radio.Serial;
        if (radio.Firmware.Length > 0 && Version.Length == 0) Version = radio.Firmware;
        RaiseDetailsChanged();
    }

    /// <summary>Applies what the Swisscom Internet-Box reported (exact model, serial, firmware).</summary>
    public void ApplySwisscomInfo(SwisscomProbe.BoxInfo box)
    {
        if (box.ModelName.Length > 0) Model.ExtraInfo["Modell"] = box.ModelName; // feeds ModelDisplay
        if (box.Serial.Length > 0) Model.SerialNumber = box.Serial;
        if (box.Firmware.Length > 0 && Version.Length == 0) Version = box.Firmware;
        RaiseDetailsChanged();
    }

    public ObservableCollection<ResourceSnapshot> History { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();

    /// <summary>True when this device exposed an SMB share port when scanned – the Details tab then
    /// offers share browsing.</summary>
    public bool HasSmb => Model.HasSmb;

    /// <summary>SMB shares of this host (lazily filled when the device is selected).</summary>
    public ObservableCollection<SmbShareVm> Shares { get; } = new();

    private string _sharesStatus = "";
    public string SharesStatus { get => _sharesStatus; private set { _sharesStatus = value; Notify(); } }
    private bool _sharesLoaded;

    /// <summary>Enumerates the device's SMB shares once (raced against a timeout so a slow/locked
    /// server can't hang the UI). A password-protected server that isn't already authenticated
    /// answers with access-denied.</summary>
    public async Task LoadSharesAsync()
    {
        if (!HasSmb || _sharesLoaded) return;
        _sharesLoaded = true;
        SharesStatus = T("Sc_SmbLoading");

        // SMB works over IPv4 – but an existing Windows session (mapped drive, stored credentials)
        // is keyed by the server NAME. \\sr1 answers instantly where \\192.168.13.1 crawls into
        // access-denied, so we try host names first and only then the bare address.
        var ip = Ipv4Address.Length > 0 ? Ipv4Address : Host;
        var candidates = new List<string>();
        if (Host.Length > 0 && !IPAddress.TryParse(Host, out _)) candidates.Add(Host);
        try
        {
            var dnsName = (await Dns.GetHostEntryAsync(ip)).HostName;
            if (dnsName.Length > 0)
            {
                candidates.Add(dnsName);
                var shortName = dnsName.Split('.')[0];
                if (!shortName.Equals(dnsName, StringComparison.OrdinalIgnoreCase)) candidates.Add(shortName);
            }
        }
        catch (System.Net.Sockets.SocketException) { /* no PTR record – the IP still gets tried */ }
        candidates.Add(ip);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool denied = false, timedOut = false;
        foreach (var candidate in candidates.Where(c => c.Length > 0 && seen.Add(c)))
        {
            try
            {
                var listTask = SmbShares.ListAsync(candidate);
                if (await Task.WhenAny(listTask, Task.Delay(TimeSpan.FromSeconds(10))) != listTask)
                {
                    timedOut = true; // NetShareEnum keeps blocking its pool thread; just move on
                    continue;
                }
                var result = await listTask;
                if (result.Status == ShareListStatus.Ok && result.Shares.Count > 0)
                {
                    foreach (var name in result.Shares) Shares.Add(new SmbShareVm(candidate, name));
                    SharesStatus = "";
                    return;
                }
                denied |= result.Status == ShareListStatus.AccessDenied;
            }
            catch { /* try the next candidate */ }
        }

        if (denied) { SharesStatus = T("Sc_SmbDenied"); return; }
        if (timedOut)
        {
            _sharesLoaded = false; // allow a retry on the next expand/selection
            SharesStatus = T("Sc_SmbTimeout");
            return;
        }
        SharesStatus = T("Sc_SmbNone");
    }

    /// <summary>Rows of the "Available updates" tab: latest version per channel.</summary>
    public ObservableCollection<ChannelUpdateVm> AvailableUpdates { get; } = new();
    private bool _availableLoaded;
    private static readonly string[] AllChannels = { "stable", "long-term", "testing", "development" };

    /// <summary>Fills the "Available updates" tab with the latest version + release date of every
    /// channel (read-only, from the public MikroTik upgrade server). Loaded once, on first view.</summary>
    public async Task LoadAvailableUpdatesAsync(CancellationToken ct = default)
    {
        if (_availableLoaded || Model.Vendor != DeviceVendor.MikroTik) return;
        _availableLoaded = true;
        var installed = StripChannelSuffix(Version);

        // First row: the currently installed firmware version (with its release date).
        if (installed.Length > 0)
            AvailableUpdates.Add(new ChannelUpdateVm(T("Upd_InstalledRow"), installed, InstalledReleaseText, isCurrent: true, isInstalled: true));

        foreach (var channel in AllChannels)
        {
            var info = await ReleaseInfoClient.GetLatestAsync(channel, ct);
            AvailableUpdates.Add(new ChannelUpdateVm(
                channel,
                info?.Version ?? T("Val_Na"),
                info is { } r ? r.ReleaseDate.ToString("yyyy-MM-dd") : "",
                string.Equals(channel, UpdateChannel, StringComparison.OrdinalIgnoreCase),
                info is { } r2 && r2.Version == installed));
        }
    }

    public string Name
    {
        get => Model.Name;
        set { Model.Name = value; Notify(); }
    }

    public string Host => Model.Host;

    /// <summary>Every address of this physical device: the primary host plus all MAC-matched extras
    /// (a host commonly has several IPv6 addresses — global, ULA, link-local, privacy).</summary>
    private IEnumerable<string> AllAddresses()
    {
        if (Model.Host.Length > 0) yield return Model.Host;
        foreach (var a in Model.AltAddresses) yield return a;
    }

    /// <summary>The device's IPv4 address (first one found across all its addresses), or "".</summary>
    public string Ipv4Address =>
        AllAddresses().FirstOrDefault(a => IPAddress.TryParse(a, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetwork) ?? "";

    /// <summary>All IPv6 addresses of the device (a NIC often has several).</summary>
    public IReadOnlyList<string> Ipv6List =>
        AllAddresses().Where(a => IPAddress.TryParse(a, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetworkV6)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>All IPv6 addresses, one per line (for the IPv6 column).</summary>
    public string Ipv6Display => string.Join(Environment.NewLine, Ipv6List);

    /// <summary>Single-line IPv6 cell: the first address plus a "(+N)" hint; the expander lists all.</summary>
    public string Ipv6Summary => Ipv6List.Count switch
    {
        0 => "",
        1 => Ipv6List[0],
        var n => $"{Ipv6List[0]}  (+{n - 1})",
    };

    /// <summary>True when the expander (+) has something to show: IPv6 addresses beyond the one in
    /// the column, SMB shares, or sub-firmware details.</summary>
    public bool HasRowDetails => Ipv6List.Count > 1 || HasSmb || FirmwareDetails.Count > 0;

    /// <summary>The IPv6 addresses the collapsed row does NOT show (all but the first); the
    /// expander lists these, so a device with a single IPv6 never repeats it.</summary>
    public IReadOnlyList<string> Ipv6Rest => Ipv6List.Skip(1).ToList();

    /// <summary>Row background (the IPv6 view's rows override this with their group colour).</summary>
    public Brush RowBackground => Brushes.Transparent;

    private bool _isExpanded;
    /// <summary>Expands the row-details area (all IPv6 addresses + SMB share buttons).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            Notify();
            if (value) _ = LoadSharesAsync(); // shares load lazily on first expand
        }
    }

    public bool HasIpv4 => Ipv4Address.Length > 0;
    public bool HasIpv6 => Ipv6List.Count > 0;

    /// <summary>Numeric key for sorting the list by IPv4 address (v6-only devices sort last).</summary>
    public uint Ipv4SortKey
    {
        get
        {
            if (IPAddress.TryParse(Ipv4Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            }
            return uint.MaxValue;
        }
    }

    /// <summary>True if any of the device's addresses equals <paramref name="ip"/>.</summary>
    public bool HasAddress(string ip) =>
        AllAddresses().Any(a => string.Equals(a, ip, StringComparison.OrdinalIgnoreCase));

    /// <summary>All addresses joined, for the text filter.</summary>
    public string AllAddressesText => string.Join(" ", AllAddresses());

    /// <summary>Re-raises the address columns after the address set changed (a MAC-matched addition).</summary>
    public void RefreshAddressDisplay()
    {
        Notify(nameof(Ipv4Address));
        Notify(nameof(Ipv6List));
        Notify(nameof(Ipv6Display));
        Notify(nameof(Ipv6Summary));
        Notify(nameof(Ipv6Rest));
        Notify(nameof(HasRowDetails));
        Notify(nameof(HasIpv4));
        Notify(nameof(HasIpv6));
        Notify(nameof(Ipv4SortKey));
    }

    /// <summary>Pings the device (its IPv4 if it has one, else the primary host).</summary>
    private async Task<bool> PingAsync(CancellationToken ct)
    {
        var host = Ipv4Address.Length > 0 ? Ipv4Address : Model.Host;
        if (host.Length == 0) return false;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1000).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or SocketException) { return false; }
    }

    /// <summary>Reachability-only refresh for devices we don't log into: green if it pings, red if not.</summary>
    public async Task<bool> RefreshReachabilityAsync(CancellationToken ct = default)
    {
        if (IsRefreshing) return Status == DeviceStatus.Online;
        IsRefreshing = true;
        try
        {
            Status = await PingAsync(ct).ConfigureAwait(false) ? DeviceStatus.Online : DeviceStatus.Offline;
            return Status == DeviceStatus.Online;
        }
        finally { IsRefreshing = false; }
    }

    /// <summary>Marks the device reachable (green) – e.g. it was just found by discovery.</summary>
    public void MarkOnline() => Status = DeviceStatus.Online;

    /// <summary>Notifies the discovery-derived properties after a later scan filled in the MAC/ports.</summary>
    public void RaiseDiscoveryChanged()
    {
        Notify(nameof(MacVendor));
        Notify(nameof(IdentifiedVendor));
        Notify(nameof(DeviceType));
        Notify(nameof(HasSmb));
        Notify(nameof(HasRowDetails));
    }

    public string ConnectionDisplay => $"{(Model.UseHttps ? "https" : "http")}://{Model.Host}:{Model.Port}";

    /// <summary>Transport used for the REST API of this device (used by the filter).</summary>
    public string TransportDisplay => Model.UseHttps ? "HTTPS" : "HTTP";

    /// <summary>Protocols this device speaks, for the "Supported protocols" column. Web entries
    /// (http/https) carry a URL and open in the browser on double-click.</summary>
    // The IPv4 view links over IPv4; the IPv6 view builds its own list per address row.
    public IReadOnlyList<ProtocolVm> SupportedProtocols =>
        ProtocolsFor(Ipv4Address.Length > 0 ? Ipv4Address : Model.Host);

    /// <summary>Protocol badges whose web links point at the given address/host.</summary>
    public IReadOnlyList<ProtocolVm> ProtocolsFor(string host)
    {
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        var list = new List<ProtocolVm>();
        if (Model.OpenPorts.Count > 0)
        {
            // Discovered device: a colour-coded badge for each recognised open service.
            foreach (var port in Model.OpenPorts.Distinct().OrderBy(p => p))
            {
                var svc = SubnetScanner.ServiceName(port);
                var url = svc is "ssh" ? $"ssh://{hostPart}" : WebUrl(port, hostPart);
                list.Add(new ProtocolVm(svc, url, ProtocolVm.BrushFor(svc)));
            }
        }
        else
        {
            // Manually added device: offer the web schemes + ssh (a click opens them).
            list.Add(new ProtocolVm("http", $"http://{hostPart}/", ProtocolVm.BrushFor("http")));
            list.Add(new ProtocolVm("https", $"https://{hostPart}/", ProtocolVm.BrushFor("https")));
            list.Add(new ProtocolVm("ssh", $"ssh://{hostPart}", ProtocolVm.BrushFor("ssh")));
        }
        return list;
    }

    private static string WebUrl(int port, string host) => port switch
    {
        80 => $"http://{host}/",
        443 => $"https://{host}/",
        8080 => $"http://{host}:8080/",
        _ => "",
    };

    /// <summary>Raw manufacturer from the MAC address / IEEE OUI list ("MAC vendor" column;
    /// shows the list's exact name, e.g. "Routerboard.com", empty if unknown).</summary>
    public string MacVendor => OuiLookup.Lookup(Model.MacAddress);

    /// <summary>Identified device vendor for the "Vendor" column: MikroTik / TP-Link when known,
    /// otherwise the manufacturer scraped from the device's web UI (empty if none).</summary>
    public string IdentifiedVendor
    {
        get
        {
            var mac = MacVendor.ToLowerInvariant();
            if (Board.Length > 0 || mac.Contains("mikrotik") || mac.Contains("routerboard")) return "MikroTik";
            if (Model.Vendor == DeviceVendor.TpLink) return "TP-Link";
            if (Model.ExtraInfo.TryGetValue("Hersteller (Web)", out var web) && web.Length > 0) return web;
            if (Model.ExtraInfo.TryGetValue("Hersteller", out var wmi) && NormalizeVendor(wmi) is { Length: > 0 } v)
                return v; // WMI manufacturer (e.g. "LENOVO" → "Lenovo")
            if (mac.Contains("philips light") || mac.Contains("signify"))
                return "Signify"; // Philips Lighting BV is Signify today
            if (mac.Contains("american power") || mac.StartsWith("apc") || mac.Contains(" apc "))
                return "APC"; // APC / American Power Conversion – almost always a UPS
            return "";
        }
    }

    /// <summary>Cleans a WMI manufacturer string: drops BIOS placeholders, fixes ALL-CAPS names.</summary>
    private static string NormalizeVendor(string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0 ||
            v.Contains("to be filled", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase)) return "";
        if (v.Length > 3 && v == v.ToUpperInvariant())
            v = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(v.ToLowerInvariant());
        return v;
    }

    /// <summary>Rough device kind for the "Type" column. TP-Link devices are switches; a device
    /// that answered the RouterOS API (has a board name) is a MikroTik router; a WMI form factor
    /// (laptop/notebook/tablet …) beats the vendor guess; otherwise guessed.</summary>
    public string DeviceType
    {
        get
        {
            if (Model.Vendor == DeviceVendor.TpLink) return T("Dev_Switch");
            if (Board.Length > 0) return T("Dev_Router");
            if (Model.ExtraInfo.TryGetValue("Bauform", out var ff))
            {
                var t = ff switch
                {
                    "Laptop" => T("Dev_Laptop"),
                    "Notebook" => T("Dev_Notebook"),
                    "Tablet" => T("Dev_Tablet"),
                    "Desktop" or "Workstation" => T("Dev_Pc"),
                    "Server" or "Server (SOHO)" or "Performance-Server" => T("Dev_Server"),
                    _ => "",
                };
                if (t.Length > 0) return t;
            }
            // The identified vendor (web-scraped) counts too – a Gardena hub is IoT even when its
            // MAC belongs to a generic radio-module maker.
            return DeviceKindText(DeviceClassifier.Guess($"{MacVendor} {IdentifiedVendor}", Model.OpenPorts));
        }
    }

    /// <summary>True for TP-Link switches (SSH connector, firmware page instead of channels).</summary>
    public bool IsTpLink => Model.Vendor == DeviceVendor.TpLink;

    /// <summary>TP-Link: the Omada download page URL for this model/revision ("" otherwise).</summary>
    public string FirmwarePageUrl => Model.Vendor == DeviceVendor.TpLink
        ? OmadaSupport.FirmwarePageUrl(Model.Model, Model.HardwareRevision)
        : "";

    /// <summary>Maps a guessed <see cref="DeviceKind"/> to its localized label ("" when unknown).</summary>
    public static string DeviceKindText(DeviceKind kind) => kind switch
    {
        DeviceKind.Router => T("Dev_Router"),
        DeviceKind.Switch => T("Dev_Switch"),
        DeviceKind.AccessPoint => T("Dev_AccessPoint"),
        DeviceKind.Printer => T("Dev_Printer"),
        DeviceKind.Nas => T("Dev_Nas"),
        DeviceKind.Pc => T("Dev_Pc"),
        DeviceKind.Phone => T("Dev_Phone"),
        DeviceKind.Camera => T("Dev_Camera"),
        DeviceKind.IoT => T("Dev_IoT"),
        DeviceKind.Server => T("Dev_Server"),
        DeviceKind.Ups => T("Dev_Ups"),
        DeviceKind.Laptop => T("Dev_Laptop"),
        DeviceKind.Notebook => T("Dev_Notebook"),
        DeviceKind.Tablet => T("Dev_Tablet"),
        _ => "",
    };

    private bool _isSelected;
    /// <summary>Ticked in the main list; batch actions (backup/update) act on marked devices.</summary>
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(); } }

    private bool _isGateway;
    /// <summary>True when this device's host is the local default gateway (row highlighted orange).</summary>
    public bool IsGateway { get => _isGateway; set { _isGateway = value; Notify(); } }

    private DeviceStatus _status = DeviceStatus.Unknown;
    public DeviceStatus Status
    {
        get => _status;
        private set { _status = value; Notify(); Notify(nameof(StatusText)); Notify(nameof(StatusBrush)); Notify(nameof(IsOffline)); }
    }

    /// <summary>True when the last query failed (row text shown red, e.g. a stored device that is
    /// no longer reachable after loading the config at startup).</summary>
    public bool IsOffline => Status == DeviceStatus.Offline;

    public string StatusText => Status switch
    {
        DeviceStatus.Online => T("St_Online"),
        DeviceStatus.Offline => T("St_Offline"),
        DeviceStatus.Busy => T("St_Busy"),
        _ => T("St_Unknown"),
    };

    public Brush StatusBrush => Status switch
    {
        DeviceStatus.Online => Brushes.ForestGreen,
        DeviceStatus.Offline => Brushes.Firebrick,
        DeviceStatus.Busy => Brushes.DarkOrange,
        _ => Brushes.Gray,
    };

    private string _version = "";
    public string Version { get => _version; private set { _version = value; Notify(); } }

    private string _board = "";
    public string Board { get => _board; private set { _board = value; Notify(); Notify(nameof(DeviceType)); Notify(nameof(IdentifiedVendor)); Notify(nameof(ModelDisplay)); } }

    /// <summary>Model shown in the "Model" column for any device: the RouterOS board / TP-Link model
    /// when known, otherwise the model learned via WMI, otherwise the model scraped from the web UI
    /// title. A leading manufacturer that already shows in the Vendor column is stripped
    /// ("Brother MFC-L2710DW" → "MFC-L2710DW"); "" when the model is only the vendor name.</summary>
    public string ModelDisplay
    {
        get
        {
            // WMI product name first ("ThinkPad P52"), with the machine-type code appended in
            // parentheses when it adds anything: "ThinkPad P52 (20M9CTO1WW)".
            Model.ExtraInfo.TryGetValue("Produkt", out var product);
            Model.ExtraInfo.TryGetValue("Modell", out var wmi);
            var model = Board.Length > 0 ? Board
                : !string.IsNullOrEmpty(product)
                    ? !string.IsNullOrEmpty(wmi) && !product.Contains(wmi, StringComparison.OrdinalIgnoreCase)
                        ? $"{product} ({wmi})"
                        : product
                : !string.IsNullOrEmpty(wmi) ? wmi
                : Model.ExtraInfo.TryGetValue("Web-Titel", out var web) ? web
                : "";
            var vendor = IdentifiedVendor;
            if (vendor.Length > 0 && model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
                model = model[vendor.Length..].TrimStart(' ', '-', ':', '/', '·', ',');
            return model.Trim();
        }
    }

    private string _uptime = "";
    public string Uptime { get => _uptime; private set { _uptime = value; Notify(); } }

    private int _cpuLoad;
    public int CpuLoad { get => _cpuLoad; private set { _cpuLoad = value; Notify(); Notify(nameof(CpuText)); } }
    public string CpuText => Status == DeviceStatus.Online || Version != "" ? $"{CpuLoad} %" : "";

    private string _memoryText = "";
    public string MemoryText { get => _memoryText; private set { _memoryText = value; Notify(); } }

    private string _latestVersion = "";
    public string LatestVersion
    {
        get => _latestVersion;
        private set { _latestVersion = value; Notify(); Notify(nameof(LatestWithChannel)); Notify(nameof(VersionIsCurrent)); }
    }

    private string _updateChannel = "";
    public string UpdateChannel
    {
        get => _updateChannel;
        private set { _updateChannel = value; Notify(); Notify(nameof(LatestWithChannel)); }
    }

    /// <summary>"Latest" column: version with channel in parentheses, e.g. "7.16.1 (stable)".</summary>
    public string LatestWithChannel =>
        _latestVersion.Length > 0 && _updateChannel.Length > 0 ? $"{_latestVersion} ({_updateChannel})" : _latestVersion;

    /// <summary>True once an update check ran and the device is on the latest version (→ green).</summary>
    public bool VersionIsCurrent => _latestVersion.Length > 0 && !_updateAvailable;

    private string _updateStatusText = "";
    public string UpdateStatusText { get => _updateStatusText; private set { _updateStatusText = value; Notify(); } }

    private string _latestReleaseText = "";
    /// <summary>Version and release date of the latest version, e.g. "7.23.1 (2. Juni 2026)".</summary>
    public string LatestReleaseText { get => _latestReleaseText; private set { _latestReleaseText = value; Notify(); } }

    private DateTime? _installedDate;
    /// <summary>Release date of the currently installed version (from its changelog).</summary>
    public string InstalledReleaseText => _installedDate is { } d ? d.ToString("yyyy-MM-dd") : "";

    private DateTime? _updateReleaseDate;
    /// <summary>Release date of the proposed update / latest version (from its changelog).</summary>
    public string UpdateReleaseText => _updateReleaseDate is { } d ? d.ToString("yyyy-MM-dd") : "";

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set { _updateAvailable = value; Notify(); Notify(nameof(VersionIsCurrent)); } }

    private string _lastError = "";
    public string LastError { get => _lastError; private set { _lastError = value; Notify(); } }

    private bool _isRefreshing;
    public bool IsRefreshing { get => _isRefreshing; private set { _isRefreshing = value; Notify(); } }

    private RouterOsClient Client =>
        _client ??= RouterOsClient.For(Model, CredentialProtector.Unprotect(Model.EncryptedPassword));

    /// <summary>True if the last refresh failed with a TLS/HTTPS handshake problem.</summary>
    public bool HadTlsError { get; private set; }

    /// <summary>Call this after changes to host/port/credentials.</summary>
    public void ResetClient()
    {
        _client?.Dispose();
        _client = null;
        Notify(nameof(Name));
        Notify(nameof(Host));
        Notify(nameof(Ipv4Address));
        Notify(nameof(Ipv6List));
        Notify(nameof(Ipv6Display));
        Notify(nameof(Ipv6Summary));
        Notify(nameof(Ipv6Rest));
        Notify(nameof(HasRowDetails));
        Notify(nameof(HasIpv4));
        Notify(nameof(HasIpv6));
        Notify(nameof(ConnectionDisplay));
        Notify(nameof(TransportDisplay));
        Notify(nameof(SupportedProtocols));
    }

    /// <summary>Switches this device from HTTPS to plain HTTP (port 80 if it was 443).</summary>
    public void SwitchToHttp()
    {
        Model.UseHttps = false;
        if (Model.Port == 443) Model.Port = 80;
        ResetClient();
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (IsRefreshing) return Status == DeviceStatus.Online;
        if (Model.Vendor == DeviceVendor.TpLink) return await RefreshTpLinkAsync(ct);
        IsRefreshing = true;
        try
        {
            var r = await Client.GetSystemResourceAsync(ct);
            Version = r.Version;
            Board = r.BoardName;
            if (Model.SerialNumber.Length == 0)
            {
                try
                {
                    Model.SerialNumber = await Client.GetSerialNumberAsync(ct);
                    Notify(nameof(SerialNumber));
                }
                catch { /* CHR/x86 has no routerboard endpoint */ }
            }
            Uptime = r.Uptime;
            CpuLoad = r.CpuLoad;
            MemoryText = r.TotalMemory > 0
                ? $"{(r.TotalMemory - r.FreeMemory) / 1048576} / {r.TotalMemory / 1048576} MB"
                : "";

            History.Add(new ResourceSnapshot
            {
                Timestamp = DateTime.Now,
                CpuLoad = r.CpuLoad,
                MemoryUsedPercent = r.MemoryUsedPercent,
            });
            while (History.Count > MaxHistory) History.RemoveAt(0);

            if (LatestVersion != "" && Version != "")
                UpdateAvailable = LatestVersion != StripChannelSuffix(Version);

            Status = DeviceStatus.Online;
            LastError = "";
            HadTlsError = false;
            _ = FetchChangelogDatesAsync(ct); // fire-and-forget; fills the release-date columns
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = Shorten(ex);
            HadTlsError = ErrorText.IsTlsProblem(ex);
            // REST failed, but keep the dot green if the host still pings (reachable, just not logged in).
            Status = await PingAsync(ct).ConfigureAwait(false) ? DeviceStatus.Online : DeviceStatus.Offline;
            return false;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Refreshes a TP-Link switch over SSH (no REST API): reads firmware + model via
    /// <c>show system-info</c>. CPU/RAM aren't collected for these devices.</summary>
    private async Task<bool> RefreshTpLinkAsync(CancellationToken ct)
    {
        IsRefreshing = true;
        try
        {
            var facts = await TpLinkSshConnector.GetFactsAsync(
                Model, CredentialProtector.Unprotect(Model.EncryptedPassword), ct);
            Version = facts.FirmwareVersion;
            Board = facts.Model.Length > 0 ? facts.Model : Model.Model;
            Status = DeviceStatus.Online;
            LastError = "";
            HadTlsError = false;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = Shorten(ex);
            Status = await PingAsync(ct).ConfigureAwait(false) ? DeviceStatus.Online : DeviceStatus.Offline;
            return false;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task<bool> LoadLogsAsync(int maxEntries, CancellationToken ct = default)
    {
        if (Model.Vendor != DeviceVendor.MikroTik) { Logs.Clear(); return true; } // no RouterOS log on non-MikroTik
        try
        {
            var entries = await Client.GetLogAsync(maxEntries, ct);
            Logs.Clear();
            // newest first
            for (int i = entries.Count - 1; i >= 0; i--) Logs.Add(entries[i]);
            LastError = "";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = Shorten(ex);
            return false;
        }
    }

    public async Task<bool> CheckUpdateAsync(CancellationToken ct = default)
    {
        if (Model.Vendor != DeviceVendor.MikroTik) return false; // RouterOS-only update check
        try
        {
            var info = await Client.CheckForUpdatesAsync(ct);
            ApplyUpdateInfo(info);
            await UpdateReleaseDateAsync(ct);
            await FetchChangelogDatesAsync(ct);
            LastError = "";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            UpdateStatusText = T("Upd_CheckFailedStatus");
            LastError = Shorten(ex);
            return false;
        }
    }

    public async Task<bool> SetChannelAsync(string channel, CancellationToken ct = default)
    {
        if (Model.Vendor != DeviceVendor.MikroTik) return false; // RouterOS-only
        try
        {
            var info = await Client.SetChannelAndCheckAsync(channel, ct);
            ApplyUpdateInfo(info);
            await UpdateReleaseDateAsync(ct);
            await FetchChangelogDatesAsync(ct);
            LastError = "";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            UpdateStatusText = T("Upd_ChannelFailedStatus");
            LastError = Shorten(ex);
            return false;
        }
    }

    /// <summary>Downloads the config export and returns (Config, Identity); null on error.</summary>
    public async Task<(string Config, string Identity)?> DownloadConfigAsync(CancellationToken ct = default)
    {
        try
        {
            var identity = await Client.GetIdentityAsync(ct);
            var config = await Client.GetConfigExportAsync(ct);
            LastError = "";
            return (config, identity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = Shorten(ex);
            return null;
        }
    }

    /// <summary>Downloads a binary full backup (.backup) via the selected method. false on error.</summary>
    public async Task<bool> DownloadFullBackupAsync(BackupMethod method, int sshPort, string localPath, CancellationToken ct = default)
    {
        try
        {
            var password = CredentialProtector.Unprotect(Model.EncryptedPassword);
            await BackupService.DownloadFullBackupAsync(Model, password, method, sshPort, localPath, log: null, ct);
            LastError = "";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = Shorten(ex);
            return false;
        }
    }

    public Task InstallUpdateAsync(CancellationToken ct = default) => Client.InstallUpdateAsync(ct);

    /// <summary>Fetches the release date of the latest version of the current channel
    /// from the MikroTik upgrade server (optional – simply empty when offline).</summary>
    private async Task UpdateReleaseDateAsync(CancellationToken ct)
    {
        if (UpdateChannel.Length == 0) { LatestReleaseText = LatestVersion; return; }
        var info = await ReleaseInfoClient.GetLatestAsync(UpdateChannel, ct);
        LatestReleaseText = info is { } r
            ? $"{r.Version} ({r.ReleaseDate.ToString("d. MMMM yyyy", GermanCulture)})"
            : LatestVersion;
    }

    /// <summary>Fills the installed/update release-date columns from the per-version changelogs (cached).</summary>
    private async Task FetchChangelogDatesAsync(CancellationToken ct)
    {
        try
        {
            var installed = StripChannelSuffix(Version);
            if (installed.Length > 0)
            {
                var d = await ChangelogClient.GetReleaseDateAsync(installed, ct);
                if (d != _installedDate) { _installedDate = d; Notify(nameof(InstalledReleaseText)); }
            }
            if (LatestVersion.Length > 0)
            {
                var d = await ChangelogClient.GetReleaseDateAsync(LatestVersion, ct);
                if (d != _updateReleaseDate) { _updateReleaseDate = d; Notify(nameof(UpdateReleaseText)); }
            }
        }
        catch (OperationCanceledException) { /* refresh cancelled */ }
    }

    private void ApplyUpdateInfo(UpdateInfo info)
    {
        UpdateChannel = info.Channel;
        LatestVersion = info.LatestVersion;
        UpdateStatusText = info.Status;
        UpdateAvailable = info.UpdateAvailable;
        if (info.InstalledVersion != "" && Version == "") Version = info.InstalledVersion;
    }

    /// <summary>"7.15.2 (stable)" → "7.15.2", so the comparison with latest-version is correct.</summary>
    private static string StripChannelSuffix(string version)
    {
        var idx = version.IndexOf(' ');
        return idx > 0 ? version[..idx] : version;
    }

    private static string Shorten(Exception ex)
    {
        var msg = ex is RouterOsApiException ? ex.Message : ErrorText.Describe(ex);
        return msg.Length > 180 ? msg[..180] + "…" : msg;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A key/value fact shown in the Details tab (from WMI / web enrichment).</summary>
public class InfoRow
{
    public InfoRow(string key, string value) { Key = key; Value = value; }
    public string Key { get; }
    public string Value { get; }
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

/// <summary>A protocol/service the device speaks, shown as a colour-coded badge. Web protocols
/// carry a URL (opened on double-click).</summary>
public class ProtocolVm
{
    public ProtocolVm(string name, string url, Brush color) { Name = name; Url = url; Color = color; }
    public string Name { get; }
    public string Url { get; }
    public Brush Color { get; }
    public bool IsWeb => Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    /// <summary>ssh badges open an interactive terminal session on click.</summary>
    public bool IsSsh => Url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
    public bool IsClickable => IsWeb || IsSsh;

    private static readonly Dictionary<string, Brush> Cache = new();

    /// <summary>Badge colour for a service, grouped by kind: secure=green, web=orange, ssh=blue,
    /// insecure=red, file=teal, MikroTik=dark, api/management=purple, else grey.</summary>
    public static Brush BrushFor(string service)
    {
        var hex = service switch
        {
            "https" or "imaps" or "ftps" or "smtps" or "api-ssl" or "submission" => "#2E9E44",
            "http" or "http-alt" => "#E67E22",
            "ssh" or "sftp" => "#3A5BA0",
            "telnet" or "ftp" => "#C0392B",
            "smb" or "netbios" or "rsync" => "#16A085",
            "winbox" => "#2B3A42",
            "api" => "#8E44AD",
            "wmi" => "#7A3EA0",
            "dns" or "snmp" or "syslog" => "#7F8C8D",
            "smtp" or "imap" => "#2980B9",
            _ => "#95A5A6",
        };
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            brush.Freeze();
            Cache[hex] = brush;
        }
        return brush;
    }
}

/// <summary>One row in the "Available updates" tab: the newest version of a channel.</summary>
public class ChannelUpdateVm
{
    public ChannelUpdateVm(string channel, string version, string releaseDate, bool isCurrent, bool isInstalled)
    {
        Channel = channel;
        Version = version;
        ReleaseDate = releaseDate;
        IsCurrent = isCurrent;
        IsInstalled = isInstalled;
    }

    public string Channel { get; }
    public string Version { get; }
    public string ReleaseDate { get; }
    /// <summary>The channel this device is set to (row shown bold).</summary>
    public bool IsCurrent { get; }
    /// <summary>This channel's newest version equals the installed version.</summary>
    public bool IsInstalled { get; }
}
