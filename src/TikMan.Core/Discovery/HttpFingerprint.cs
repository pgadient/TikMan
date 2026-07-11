using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>Fingerprints a host's web interface (best-effort, read-only, one short GET): the HTTP
/// <c>Server</c> header, the page &lt;title&gt; (often the exact model, e.g. "DS220j — Synology
/// DiskStation"), and a manufacturer guessed from the title / meta tags / server string.</summary>
public static partial class HttpFingerprint
{
    public readonly record struct HttpInfo(string WebServer, string Title, string Vendor);

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta[^>]*?content=[""']([^""']{1,140})[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaContentRegex();

    // Keyword (lower-case) → manufacturer shown. First match wins; checked against title + metas + Server.
    private static readonly (string Key, string Name)[] Brands =
    {
        ("synology", "Synology"), ("diskstation", "Synology"), ("qnap", "QNAP"), ("truenas", "TrueNAS"),
        ("freenas", "TrueNAS"), ("unraid", "Unraid"), ("western digital", "Western Digital"), ("mycloud", "WD My Cloud"),
        ("fritz!box", "AVM FRITZ!Box"), ("fritzbox", "AVM FRITZ!Box"), (" avm", "AVM"),
        ("unifi", "Ubiquiti"), ("ubiquiti", "Ubiquiti"), ("edgerouter", "Ubiquiti"), ("edgeos", "Ubiquiti"),
        ("hikvision", "Hikvision"), ("dahua", "Dahua"), ("axis", "Axis"), ("reolink", "Reolink"), ("mobotix", "Mobotix"),
        ("tp-link", "TP-Link"), ("tplink", "TP-Link"), ("omada", "TP-Link Omada"), ("archer", "TP-Link"),
        ("netgear", "Netgear"), ("zyxel", "Zyxel"), ("d-link", "D-Link"), ("asuswrt", "ASUS"), ("asus", "ASUS"),
        ("mikrotik", "MikroTik"), ("routeros", "MikroTik"), ("openwrt", "OpenWrt"), ("dd-wrt", "DD-WRT"),
        ("pfsense", "pfSense"), ("opnsense", "OPNsense"), ("proxmox", "Proxmox"), ("truecharts", "TrueNAS"),
        ("laserjet", "HP"), ("officejet", "HP"), ("hewlett", "HP"), ("hp inc", "HP"),
        ("canon", "Canon"), ("brother", "Brother"), ("epson", "Epson"), ("lexmark", "Lexmark"), ("kyocera", "Kyocera"),
        ("sonos", "Sonos"), ("philips hue", "Philips Hue"), ("shelly", "Shelly"), ("sonoff", "Sonoff"), ("tasmota", "Tasmota"),
        ("mystrom", "myStrom"), ("netatmo", "Netatmo"), ("fronius", "Fronius"), ("gardena", "Gardena"),
        ("internet-box", "Swisscom"), ("internet box", "Swisscom"), ("swisscom", "Swisscom"),
        ("sunrise", "Sunrise"), ("livebox", "Orange"), ("speedport", "Telekom"), ("connect box", "UPC/Vodafone"),
        ("home assistant", "Home Assistant"), ("openmediavault", "OpenMediaVault"), ("plex", "Plex"),
    };

    public static async Task<HttpInfo> ProbeAsync(string host, CancellationToken ct = default)
    {
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host; // bracket IPv6
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // devices use self-signed certs
                    AllowAutoRedirect = true,
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TikMan/1.0");

                using var resp = await http.GetAsync($"{scheme}://{hostPart}/",
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                var server = (resp.Headers.Server?.ToString() ?? "").Trim();
                var html = await ReadHtmlAsync(resp, ct).ConfigureAwait(false);
                var title = CleanTitle(ExtractTitle(html));
                var vendor = BrandFrom($"{title} {server} {ExtractMetas(html)}");

                if (server.Length > 0 || title.Length > 0 || vendor.Length > 0)
                    return new HttpInfo(server, title, vendor);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // unreachable on this scheme – try the next, then give up
            }
        }
        return new HttpInfo("", "", "");
    }

    private static async Task<string> ReadHtmlAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[32768];
            int read = 0, n;
            while (read < buffer.Length &&
                   (n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false)) > 0)
                read += n;
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return ""; }
    }

    private static string ExtractTitle(string html)
    {
        var m = TitleRegex().Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }

    private static string ExtractMetas(string html)
    {
        var sb = new StringBuilder();
        foreach (Match m in MetaContentRegex().Matches(html))
        {
            sb.Append(WebUtility.HtmlDecode(m.Groups[1].Value)).Append(' ');
            if (sb.Length > 600) break;
        }
        return sb.ToString();
    }

    /// <summary>Keeps a title that looks like a device name; drops empty/very long/generic ones.</summary>
    private static string CleanTitle(string title)
    {
        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (title.Length is < 2 or > 80) return "";
        return title.ToLowerInvariant() is "login" or "index" or "home" or "welcome" or "anmelden" ? "" : title;
    }

    private static string BrandFrom(string haystack)
    {
        var lower = haystack.ToLowerInvariant();
        foreach (var (key, name) in Brands)
            if (lower.Contains(key)) return name;
        return "";
    }
}
