namespace TikMan.Core.Models;

/// <summary>Result of a discovery run (MNDP or subnet scan).</summary>
public class DiscoveredDevice
{
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Version { get; set; } = "";
    public string Board { get; set; } = "";
    public string Platform { get; set; } = "";
    /// <summary>"MNDP" or "Scan".</summary>
    public string Source { get; set; } = "";
    public TimeSpan? Uptime { get; set; }
    public List<int> OpenPorts { get; set; } = new();
    public bool IsLikelyMikroTik { get; set; }
}
