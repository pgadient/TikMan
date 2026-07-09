using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

    public DeviceViewModel(Device model) => Model = model;

    public ObservableCollection<ResourceSnapshot> History { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();

    public string Name
    {
        get => Model.Name;
        set { Model.Name = value; Notify(); }
    }

    public string Host => Model.Host;
    public string ConnectionDisplay => $"{(Model.UseHttps ? "https" : "http")}://{Model.Host}:{Model.Port}";

    /// <summary>Transport used for the REST API of this device (shown as a column).</summary>
    public string TransportDisplay => Model.UseHttps ? "HTTPS" : "HTTP";

    /// <summary>Manufacturer resolved offline from the MAC address (empty if unknown).</summary>
    public string Vendor => OuiLookup.Lookup(Model.MacAddress);

    /// <summary>Rough device kind for the "Type" column. A device that answered the RouterOS API
    /// (has a board name) is a MikroTik router/switch; otherwise it's guessed from the vendor.</summary>
    public string DeviceType => Board.Length > 0
        ? T("Dev_Router")
        : DeviceKindText(DeviceClassifier.Guess(Vendor, Array.Empty<int>()));

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
        private set { _status = value; Notify(); Notify(nameof(StatusText)); Notify(nameof(StatusBrush)); }
    }

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
    public string Board { get => _board; private set { _board = value; Notify(); Notify(nameof(DeviceType)); } }

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
        Notify(nameof(ConnectionDisplay));
        Notify(nameof(TransportDisplay));
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
        IsRefreshing = true;
        try
        {
            var r = await Client.GetSystemResourceAsync(ct);
            Version = r.Version;
            Board = r.BoardName;
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
            Status = DeviceStatus.Offline;
            LastError = Shorten(ex);
            HadTlsError = ErrorText.IsTlsProblem(ex);
            return false;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task<bool> LoadLogsAsync(int maxEntries, CancellationToken ct = default)
    {
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
