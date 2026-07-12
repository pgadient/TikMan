using System.Text.Json;
using System.Text.Json.Serialization;
using TikMan.Core.Models;

namespace TikMan.Core.Storage;

/// <summary>Persisted app data (device list + settings).</summary>
public class AppData
{
    public int Version { get; set; } = 1;
    public int PollIntervalSeconds { get; set; } = 30;
    public bool AutoRefreshEnabled { get; set; }
    public bool LogAutoRefresh { get; set; } = true;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public BackupMethod BackupMethod { get; set; } = BackupMethod.Auto;
    public int SshPort { get; set; } = 22;
    /// <summary>Default username offered for devices added from a scan.</summary>
    public string DefaultUsername { get; set; } = "admin";
    /// <summary>Default password (DPAPI-encrypted) for devices added from a scan.</summary>
    public string DefaultEncryptedPassword { get; set; } = "";
    /// <summary>Update channel used for devices that don't override it themselves.</summary>
    public string DefaultUpdateChannel { get; set; } = "stable";
    /// <summary>Default for new devices: accept self-signed/invalid TLS certificates (common on a
    /// LAN with MikroTik's default self-signed cert). On by default.</summary>
    public bool DefaultIgnoreCertErrors { get; set; } = true;
    /// <summary>Whether the list shows a device's IPv4 and IPv6 address combined in one row.
    /// Off by default (matched addresses are shown on separate rows unless the user turns it on).</summary>
    public bool CombineAddresses { get; set; }
    /// <summary>Allow logging in over plain HTTP when HTTPS fails (credentials then travel in clear
    /// text). Off by default; when on, TLS-failed devices are retried over HTTP on the next refresh.</summary>
    public bool AllowHttpFallback { get; set; }
    /// <summary>Force the problem report to use the default-mail (mailto) path with the log inline,
    /// instead of preferring Outlook Classic with a file attachment. Off by default.</summary>
    public bool ForceMailFallback { get; set; }
    /// <summary>Coffee button size: "normal", "small", or "off".</summary>
    public string CoffeeButton { get; set; } = "normal";
    /// <summary>Expand device rows by default, so all IPv6 addresses and SMB shares are visible
    /// without clicking the + expander.</summary>
    public bool ExpandRowsByDefault { get; set; }
    /// <summary>Which address tab the list last showed: false = IPv4 (default), true = IPv6.</summary>
    public bool ShowIpv6View { get; set; }
    /// <summary>Open ssh sessions with an external client (see <see cref="ExternalSshClientPath"/>)
    /// instead of the built-in OpenSSH terminal.</summary>
    public bool UseExternalSshClient { get; set; }
    /// <summary>Path to the external SSH client executable (e.g. PuTTY).</summary>
    public string ExternalSshClientPath { get; set; } = "";
    /// <summary>When false (default), the device list is not written to disk – devices only live
    /// for the current session. When true, the list and its config are persisted.</summary>
    public bool PersistDeviceList { get; set; }
    public List<Device> Devices { get; set; } = new();
}

/// <summary>Loads/saves the app data as JSON under %AppData%\TikMan.</summary>
public static class DeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }, // store enums as readable strings
    };

    public static string StorageDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TikMan");

    public static string StorageFile => Path.Combine(StorageDirectory, "devices.json");

    public static AppData Load()
    {
        try
        {
            if (File.Exists(StorageFile))
            {
                var text = File.ReadAllText(StorageFile);
                var data = JsonSerializer.Deserialize<AppData>(text) ?? new AppData();
                // Grandfather existing users: a config written before the persistence toggle existed
                // and that already holds devices must keep persisting them – otherwise upgrading to a
                // build with the (default-off) toggle would silently wipe the device list on the next save.
                if (data.Devices.Count > 0 && !text.Contains("\"PersistDeviceList\"", StringComparison.Ordinal))
                    data.PersistDeviceList = true;
                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // don't overwrite a corrupt file, set it aside instead (keep the first one if it exists)
            try
            {
                var backup = StorageFile + ".corrupt";
                if (!File.Exists(backup)) File.Move(StorageFile, backup);
            }
            catch { }
        }
        return new AppData();
    }

    public static void Save(AppData data)
    {
        Directory.CreateDirectory(StorageDirectory);
        var tmp = StorageFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tmp, StorageFile, overwrite: true);
    }

    /// <summary>Deletes the stored config so the next start is like a first run.</summary>
    public static void DeleteConfig()
    {
        foreach (var path in new[] { StorageFile, StorageFile + ".tmp", StorageFile + ".corrupt" })
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
