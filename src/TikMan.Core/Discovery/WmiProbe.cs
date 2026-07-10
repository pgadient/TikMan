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

    private static string FormFactor(int pcSystemType) => pcSystemType switch
    {
        1 => "Desktop",
        2 => "Notebook",
        3 => "Workstation",
        4 => "Server",
        5 => "Server (SOHO)",
        6 => "Appliance-PC",
        7 => "Performance-Server",
        8 => "Tablet/Maximum",
        _ => $"Typ {pcSystemType}",
    };
}
