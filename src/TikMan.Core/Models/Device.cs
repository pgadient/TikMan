namespace TikMan.Core.Models;

/// <summary>Persisted configuration of a monitored device.</summary>
public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 443;
    public bool UseHttps { get; set; } = true;
    public bool IgnoreCertErrors { get; set; } = true;
    public string Username { get; set; } = "admin";
    /// <summary>DPAPI-encrypted (Base64), never plaintext.</summary>
    public string EncryptedPassword { get; set; } = "";
    public bool MonitoringEnabled { get; set; } = true;
    public string MacAddress { get; set; } = "";
    public string Notes { get; set; } = "";
}
