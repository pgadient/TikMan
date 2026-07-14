namespace TikMan.Core.Discovery;

/// <summary>Tells a virtual machine from bare metal by its MAC address. Hypervisors hand their
/// guests a NIC from their own registered OUI range, so the first three bytes give the host away –
/// no login, no agent, no guesswork.</summary>
public static class Virtualization
{
    // OUI (first three MAC bytes, upper-case, no separators) → hypervisor.
    private static readonly Dictionary<string, string> Hypervisors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00155D"] = "Hyper-V",
        ["000569"] = "VMware", ["000C29"] = "VMware", ["001C14"] = "VMware", ["005056"] = "VMware",
        ["080027"] = "VirtualBox", ["0A0027"] = "VirtualBox",
        ["525400"] = "KVM/QEMU",       // also Proxmox
        ["00163E"] = "Xen",
        ["001C42"] = "Parallels",
        ["001A4A"] = "oVirt/RHEV",
        ["506B8D"] = "Nutanix",
        ["024200"] = "Docker",
    };

    /// <summary>The hypervisor behind a MAC ("Hyper-V", "VMware", …), or "" for bare metal / unknown.</summary>
    public static string Hypervisor(string? mac)
    {
        var prefix = Normalize(mac);
        return prefix.Length == 6 && Hypervisors.TryGetValue(prefix, out var name) ? name : "";
    }

    /// <summary>True when the MAC belongs to a hypervisor's virtual-NIC range.</summary>
    public static bool IsVirtual(string? mac) => Hypervisor(mac).Length > 0;

    /// <summary>The first three bytes of a MAC as six upper-case hex digits, whatever the separators
    /// ("00:15:5D:FA:21:00" → "00155D"); "" when there aren't three bytes to read.</summary>
    private static string Normalize(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";
        var chars = new char[6];
        int n = 0;
        foreach (var ch in mac)
        {
            if (!Uri.IsHexDigit(ch)) continue;
            chars[n++] = char.ToUpperInvariant(ch);
            if (n == 6) return new string(chars);
        }
        return "";
    }
}
