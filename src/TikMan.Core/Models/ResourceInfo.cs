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

    /// <summary>Memory usage as a plain percentage, for devices that report only that and not byte totals
    /// (a TP-Link switch gives "19%", never used/total). Null when byte figures are available – then
    /// <see cref="MemoryUsedPercent"/> computes it from them.</summary>
    public double? MemoryPercent { get; set; }

    public double MemoryUsedPercent =>
        MemoryPercent ?? (TotalMemory > 0 ? 100.0 * (TotalMemory - FreeMemory) / TotalMemory : 0);

    /// <summary>Whether this device reports memory at all. False for gear that exposes CPU but no memory
    /// metric – an old ZyNOS switch (a GS2200 on V3.80 has no memory command whatsoever). Without this flag a
    /// missing reading is indistinguishable from a real 0 %: the cell would show "0%" and the history chart
    /// would draw a flat line along the bottom. Both consumers check this and show "unavailable" / draw
    /// nothing instead.</summary>
    public bool HasMemory => MemoryPercent is not null || TotalMemory > 0;
}

/// <summary>A single data point for the monitoring history.</summary>
public class ResourceSnapshot
{
    public DateTime Timestamp { get; set; }
    public int CpuLoad { get; set; }
    public double MemoryUsedPercent { get; set; }

    /// <summary>Carried from <see cref="ResourceInfo.HasMemory"/> so the chart can skip the RAM series for a
    /// device that reports no memory, rather than plotting a false 0 % line.</summary>
    public bool HasMemory { get; set; } = true;
}
