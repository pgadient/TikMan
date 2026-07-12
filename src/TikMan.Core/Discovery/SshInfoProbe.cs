using System.Text.RegularExpressions;
using Renci.SshNet.Common;
using TikMan.Core.Api;

namespace TikMan.Core.Discovery;

/// <summary>Generic SSH probe for consumer/SMB LAN gear (Zyxel, Netgear, D-Link, PLANET, Cisco
/// small business, HPE/Aruba, Ubiquiti, …): logs in with the device's credentials, runs the
/// vendor's most likely "show" command first and parses model / serial / firmware out of the
/// answer. Read-only – only "show"-style commands are ever sent.</summary>
public static partial class SshInfoProbe
{
    public readonly record struct SshDeviceInfo(string Model, string Serial, string Firmware);

    /// <summary>The probe commands ordered by likelihood for the given vendor: e.g. Zyxel answers
    /// "show system-information", D-Link "show switch", most others "show version".</summary>
    public static string[] CommandsFor(string vendorHint)
    {
        var v = (vendorHint ?? "").ToLowerInvariant();
        if (v.Contains("tp-link") || v.Contains("tplink") || v.Contains("pharos")) return ["show system-info", "show version"];
        if (v.Contains("zyxel")) return ["show system-information", "show version"];
        if (v.Contains("d-link") || v.Contains("dlink")) return ["show switch", "show version"];
        if (v.Contains("netgear")) return ["show version", "show system"];
        if (v.Contains("planet")) return ["show version", "show system"];
        if (v.Contains("cisco")) return ["show version", "show inventory"];
        if (v.Contains("aruba") || v.Contains("hewlett") || v.Contains("hpe")) return ["show system", "show version"];
        if (v.Contains("ubiquiti")) return ["show version", "info"];
        // Unknown vendor: broad sweep, most common first.
        return ["show version", "show system-information", "show system", "show switch"];
    }

    public static async Task<SshDeviceInfo?> QueryAsync(string host, int port, string user, string password,
        string vendorHint, CancellationToken ct = default)
    {
        try
        {
            var results = await SshExec.RunAsync(host, port, user, password, CommandsFor(vendorHint),
                stopWhen: output => HasAnyFact(Parse(output)), ct).ConfigureAwait(false);
            foreach (var (_, output) in results)
            {
                var parsed = Parse(output);
                if (HasAnyFact(parsed)) return parsed;
            }
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or SshException or System.Net.Sockets.SocketException)
        {
            return null; // unreachable / login refused / stalled – strictly best-effort
        }
    }

    private static bool HasAnyFact(SshDeviceInfo info) =>
        info.Model.Length > 0 || info.Serial.Length > 0 || info.Firmware.Length > 0;

    [GeneratedRegex(@"^\s*(?:model|system model|device model|machine model|model name|product name|device type|machine type)\s*(?:name|id)?[ .:\-\t]*(\S[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"^\s*serial\s*(?:number|no\.?|num)?[ .:\-\t]*(\S[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SerialRegex();

    [GeneratedRegex(@"^\s*(?:firmware|software|sw|image|loader)?\s*version[ .:\-\t]*(\S[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^\s*(?:firmware|software)\s*(?:version)?[ .:\-\t]*(\S[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FirmwareRegex();

    /// <summary>Label-driven parse over a "show" output; survives ":", "-" and Netgear's
    /// "Software Version.............1.0.5.5" dotted fills.</summary>
    public static SshDeviceInfo Parse(string output)
    {
        var model = Clip(ModelRegex().Match(output).Groups[1].Value);
        var serial = Clip(SerialRegex().Match(output).Groups[1].Value);
        var firmware = Clip(FirmwareRegex().Match(output).Groups[1].Value);
        if (firmware.Length == 0) firmware = Clip(VersionRegex().Match(output).Groups[1].Value);
        return new SshDeviceInfo(model, serial, firmware);
    }

    private static string Clip(string value)
    {
        var v = Regex.Replace(value, @"\s+", " ").Trim().TrimEnd('.');
        return v.Length > 60 ? v[..60] : v;
    }
}
