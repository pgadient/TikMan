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

    /// <summary>SMB share names offered by this device (empty when it has no SMB, or the platform can't
    /// enumerate them). Filled opportunistically during probing so the UI can offer them as shortcuts.</summary>
    public List<string> Shares { get; set; } = new();
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

    /// <summary>A detached copy, with its own collections. Hand this out to readers instead of the live
    /// instance: the fleet's lock protects the device <i>list</i>, not the objects in it, so a caller that
    /// walks <see cref="ExtraInfo"/> or <see cref="OpenPorts"/> outside the lock would be reading a
    /// dictionary that a background probe is resizing underneath it – a corrupt read or a hang, not an
    /// exception you would notice.</summary>
    public Device Clone() => new()
    {
        Id = Id, Vendor = Vendor, Model = Model, HardwareRevision = HardwareRevision,
        Name = Name, Host = Host, Port = Port, UseHttps = UseHttps,
        IgnoreCertErrors = IgnoreCertErrors, SshPort = SshPort, Username = Username,
        EncryptedPassword = EncryptedPassword, MonitoringEnabled = MonitoringEnabled,
        HasSmb = HasSmb, SerialNumber = SerialNumber, MacAddress = MacAddress,
        Notes = Notes, UpdateChannel = UpdateChannel,
        AltAddresses = new List<string>(AltAddresses),
        Shares = new List<string>(Shares),
        OpenPorts = new List<int>(OpenPorts),
        ExtraInfo = new Dictionary<string, string>(ExtraInfo),
    };
}
