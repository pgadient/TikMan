using System.Net;
using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>Asks a Frontier-Silicon based internet radio (Teufel, Hama, Roberts, …) for its
/// friendly name, firmware and serial. <c>GET /device</c> answers without any login; the UPnP
/// description on :8080 adds the serial number. Read-only and best-effort.</summary>
public static partial class FrontierSiliconProbe
{
    public readonly record struct RadioInfo(string Name, string Vendor, string Firmware, string Serial);

    [GeneratedRegex(@"<friendlyName>\s*(.*?)\s*</friendlyName>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FriendlyNameRegex();

    [GeneratedRegex(@"<version>\s*(.*?)\s*</version>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"<serialNumber>\s*(.*?)\s*</serialNumber>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SerialRegex();

    public static async Task<RadioInfo?> QueryAsync(string host, CancellationToken ct = default)
    {
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TikMan/1.0");

        string name, version;
        try
        {
            var xml = await http.GetStringAsync($"http://{hostPart}/device", ct).ConfigureAwait(false);
            name = WebUtility.HtmlDecode(FriendlyNameRegex().Match(xml).Groups[1].Value);
            version = VersionRegex().Match(xml).Groups[1].Value;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null; // not a Frontier Silicon device
        }
        if (name.Length == 0 && version.Length == 0) return null;

        string serial = "";
        try
        {
            var dd = await http.GetStringAsync($"http://{hostPart}:8080/dd.xml", ct).ConfigureAwait(false);
            serial = SerialRegex().Match(dd).Groups[1].Value.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // no UPnP description – name/firmware are enough
        }

        // "Teufel Radio 3sixty 305890626ef8" → drop the MAC suffix; the brand is the first word.
        name = Regex.Replace(name, @"\s*[0-9a-fA-F]{12}\s*$", "").Trim();
        var vendor = name.Contains(' ') ? name[..name.IndexOf(' ')] : "";

        // "ir-mmi-FS2026-0500-0500_V2.12.21.EX70137-1A49" → "2.12.21"
        var fw = Regex.Match(version, @"_V([0-9][0-9.]*[0-9])").Groups[1].Value;
        if (fw.Length == 0) fw = version;

        return new RadioInfo(name, vendor, fw, serial);
    }
}
