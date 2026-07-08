using System.Text.Json;
using System.Text.Json.Serialization;
using TikMan.Core.Models;

namespace TikMan.Core.Storage;

/// <summary>Persisted app data (device list + settings).</summary>
public class AppData
{
    public int Version { get; set; } = 1;
    public int PollIntervalSeconds { get; set; } = 30;
    public bool AutoRefreshEnabled { get; set; } = true;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public BackupMethod BackupMethod { get; set; } = BackupMethod.Auto;
    public int SshPort { get; set; } = 22;
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
                return JsonSerializer.Deserialize<AppData>(File.ReadAllText(StorageFile)) ?? new AppData();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // don't overwrite a corrupt file, set it aside instead
            try { File.Move(StorageFile, StorageFile + ".corrupt", overwrite: true); } catch { }
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
}
