using System.Management;
using System.Runtime.Versioning;

namespace TikMan.Core.Discovery;

/// <summary>Reads PC details (manufacturer, model, form factor, OS) from a Windows host over WMI
/// (DCOM/RPC, port 135). Uses the current Windows identity, so it only succeeds where the caller is
/// authorised (same domain/workgroup admin) – otherwise it returns nothing. Strictly best-effort.</summary>
[SupportedOSPlatform("windows")]
public static class WmiProbe
{
    public static async Task<Dictionary<string, string>> QueryAsync(string host, CancellationToken ct = default)
    {
        var info = new Dictionary<string, string>();
        try
        {
            await Task.Run(() =>
            {
                var options = new ConnectionOptions
                {
                    Timeout = TimeSpan.FromSeconds(5),
                    Impersonation = ImpersonationLevel.Impersonate,
                    EnablePrivileges = true,
                };
                var scope = new ManagementScope($@"\\{host}\root\cimv2", options);
                scope.Connect();

                foreach (ManagementBaseObject o in Query(scope, "SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem"))
                {
                    Put(info, "Hersteller", o["Manufacturer"]);
                    Put(info, "Modell", o["Model"]);
                    if (o["PCSystemType"] is { } t) info["Bauform"] = FormFactor(Convert.ToInt32(t));
                }
                foreach (ManagementBaseObject o in Query(scope, "SELECT Caption FROM Win32_OperatingSystem"))
                    Put(info, "OS", o["Caption"]);

                // The chassis type tells laptop/notebook/tablet apart (finer than PCSystemType).
                foreach (ManagementBaseObject o in Query(scope, "SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                    if (o["ChassisTypes"] is ushort[] { Length: > 0 } types && Chassis(types[0]) is { Length: > 0 } c)
                        info["Bauform"] = c;

                // BIOS serial (e.g. Lenovo "PF1MA5TJ") and the friendly product name ("ThinkPad P52" –
                // Win32_ComputerSystem.Model only holds the machine-type code like "20M9CTO1WW").
                foreach (ManagementBaseObject o in Query(scope, "SELECT SerialNumber FROM Win32_BIOS"))
                    if (Meaningful(o["SerialNumber"]) is { } sn) info["Seriennummer"] = sn;
                foreach (ManagementBaseObject o in Query(scope, "SELECT Version FROM Win32_ComputerSystemProduct"))
                    if (Meaningful(o["Version"]) is { } product) info["Produkt"] = product;
            }, ct).WaitAsync(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                      or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            // access denied / unreachable / timeout – nothing to add
        }
        return info;
    }

    private static ManagementObjectCollection Query(ManagementScope scope, string wql) =>
        new ManagementObjectSearcher(scope, new ObjectQuery(wql)).Get();

    private static void Put(Dictionary<string, string> info, string key, object? value)
    {
        var s = value?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(s)) info[key] = s;
    }

    /// <summary>Returns the trimmed value, or null for the usual BIOS placeholder junk.</summary>
    private static string? Meaningful(object? value)
    {
        var s = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Contains("to be filled", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("default string", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("system version", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("system serial", StringComparison.OrdinalIgnoreCase) ||
               s is "None" or "0" ? null : s;
    }

    private static string FormFactor(int pcSystemType) => pcSystemType switch
    {
        1 => "Desktop",
        2 => "Notebook",
        3 => "Workstation",
        4 => "Server",
        5 => "Server (SOHO)",
        6 => "Appliance-PC",
        7 => "Performance-Server",
        8 => "Tablet",
        _ => $"Typ {pcSystemType}",
    };

    /// <summary>SMBIOS chassis type → form factor ("" when it adds nothing).</summary>
    private static string Chassis(int type) => type switch
    {
        8 or 9 => "Laptop",              // Portable / Laptop
        10 or 14 => "Notebook",          // Notebook / Sub Notebook
        30 or 31 or 32 => "Tablet",      // Tablet / Convertible / Detachable
        3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 35 or 36 => "Desktop",
        17 or 23 or 28 or 29 => "Server",
        _ => "",
    };
}
