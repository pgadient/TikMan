namespace TikMan.Core.Models;

/// <summary>Representation of /system/package/update.</summary>
public class UpdateInfo
{
    public string Channel { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string Status { get; set; } = "";

    public bool UpdateAvailable =>
        LatestVersion != "" && InstalledVersion != "" && LatestVersion != InstalledVersion;
}
