namespace TikMan.Core.Models;

/// <summary>Representation of /system/resource.</summary>
public class ResourceInfo
{
    public string Version { get; set; } = "";
    public string BoardName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string ArchitectureName { get; set; } = "";
    public string Uptime { get; set; } = "";
    public int CpuLoad { get; set; }
    public long FreeMemory { get; set; }
    public long TotalMemory { get; set; }
    public long FreeHddSpace { get; set; }
    public long TotalHddSpace { get; set; }

    public double MemoryUsedPercent =>
        TotalMemory > 0 ? 100.0 * (TotalMemory - FreeMemory) / TotalMemory : 0;
}

/// <summary>A single data point for the monitoring history.</summary>
public class ResourceSnapshot
{
    public DateTime Timestamp { get; set; }
    public int CpuLoad { get; set; }
    public double MemoryUsedPercent { get; set; }
}
