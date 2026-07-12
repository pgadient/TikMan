namespace TikMan.Core.Models;

/// <summary>How TikMan talks to a device. MikroTik uses the RouterOS REST API; TP-Link managed
/// switches have no REST API, so we query them over SSH (per-vendor connector).</summary>
public enum DeviceVendor
{
    MikroTik,
    TpLink,
}

/// <summary>Persisted configuration of a monitored device.</summary>
public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Which connector to use. Determines REST (MikroTik) vs SSH (TP-Link).</summary>
    public DeviceVendor Vendor { get; set; } = DeviceVendor.MikroTik;
    /// <summary>TP-Link: model slug for the firmware page, e.g. "tl-sg2008".</summary>
    public string Model { get; set; } = "";
    /// <summary>TP-Link: hardware revision for the firmware page, e.g. "v3".</summary>
    public string HardwareRevision { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Further addresses of the same physical device (matched by MAC): the other-family
    /// address(es), including a device's multiple IPv6 addresses (global, ULA, link-local, privacy).</summary>
    public List<string> AltAddresses { get; set; } = new();
    public int Port { get; set; } = 443;
    public bool UseHttps { get; set; } = true;
    public bool IgnoreCertErrors { get; set; } = true;
    /// <summary>SSH port for backups / the info probe / the terminal (separate from the REST
    /// <see cref="Port"/>, which is 443/80 for MikroTik).</summary>
    public int SshPort { get; set; } = 22;
    public string Username { get; set; } = "admin";
    /// <summary>DPAPI-encrypted (Base64), never plaintext.</summary>
    public string EncryptedPassword { get; set; } = "";
    public bool MonitoringEnabled { get; set; } = true;
    /// <summary>Whether an SMB/Windows file-share port was seen when this device was scanned – lets
    /// the main view offer share browsing without re-probing.</summary>
    public bool HasSmb { get; set; }
    /// <summary>Hardware serial number (RouterBOARD, Brother maintenance page, …).</summary>
    public string SerialNumber { get; set; } = "";
    /// <summary>Open TCP ports seen during discovery (drives the type guess and protocol chips).</summary>
    public List<int> OpenPorts { get; set; } = new();
    /// <summary>Extra facts learned during discovery (WMI manufacturer/model/OS, web server, …),
    /// shown as key/value rows in the Details tab.</summary>
    public Dictionary<string, string> ExtraInfo { get; set; } = new();
    public string MacAddress { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Preferred RouterOS update channel for this device. Empty = use the global default
    /// (<see cref="Storage.AppData.DefaultUpdateChannel"/>). Only meaningful for MikroTik devices.</summary>
    public string UpdateChannel { get; set; } = "";
}
