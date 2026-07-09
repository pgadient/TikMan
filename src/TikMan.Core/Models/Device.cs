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
    /// <summary>Secondary address of the same physical device in the other IP family (e.g. the IPv6
    /// address when Host is IPv4). Informational; matched by MAC when adding from a scan. "" if none.</summary>
    public string AltAddress { get; set; } = "";
    public int Port { get; set; } = 443;
    public bool UseHttps { get; set; } = true;
    public bool IgnoreCertErrors { get; set; } = true;
    public string Username { get; set; } = "admin";
    /// <summary>DPAPI-encrypted (Base64), never plaintext.</summary>
    public string EncryptedPassword { get; set; } = "";
    public bool MonitoringEnabled { get; set; } = true;
    public string MacAddress { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Preferred RouterOS update channel for this device. Empty = use the global default
    /// (<see cref="Storage.AppData.DefaultUpdateChannel"/>). Only meaningful for MikroTik devices.</summary>
    public string UpdateChannel { get; set; } = "";
}
