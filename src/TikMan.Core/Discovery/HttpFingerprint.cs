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

    [GeneratedRegex(@"location(?:\.href)?\s*=\s*[""'](https?://[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex JsRedirectRegex();

    [GeneratedRegex(@"<meta[^>]*http-equiv=[""']refresh[""'][^>]*url=([^""'>;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();

    [GeneratedRegex(@"<meta[^>]*(?:property|name)=[""'](?:og:title|application-name)[""'][^>]*content=[""']([^""']{1,80})[""']", RegexOptions.IgnoreCase)]
    private static partial Regex AltTitleRegex();

    [GeneratedRegex(@"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H1Regex();

    // Keyword (lower-case) → manufacturer shown. First match wins; checked against title + metas + Server.
    private static readonly (string Key, string Name)[] Brands =
    {
        ("synology", "Synology"), ("diskstation", "Synology"), ("qnap", "QNAP"), ("truenas", "TrueNAS"),
        ("freenas", "TrueNAS"), ("unraid", "Unraid"), ("western digital", "Western Digital"), ("mycloud", "WD My Cloud"),
        ("fritz!box", "AVM FRITZ!Box"), ("fritzbox", "AVM FRITZ!Box"), (" avm", "AVM"),
        ("unifi", "Ubiquiti"), ("ubiquiti", "Ubiquiti"), ("edgerouter", "Ubiquiti"), ("edgeos", "Ubiquiti"),
        ("hikvision", "Hikvision"), ("dahua", "Dahua"), ("axis", "Axis"), ("reolink", "Reolink"), ("mobotix", "Mobotix"),
        ("tp-link", "TP-Link"), ("tplink", "TP-Link"), ("omada", "TP-Link Omada"), ("archer", "TP-Link"),
        ("pharos", "TP-Link Pharos"),
        ("netgear", "Netgear"), ("zyxel", "Zyxel"), ("d-link", "D-Link"), ("asuswrt", "ASUS"), ("asus", "ASUS"),
        ("mikrotik", "MikroTik"), ("routeros", "MikroTik"), ("openwrt", "OpenWrt"), ("dd-wrt", "DD-WRT"),
        ("pfsense", "pfSense"), ("opnsense", "OPNsense"), ("proxmox", "Proxmox"), ("truecharts", "TrueNAS"),
        ("laserjet", "HP"), ("officejet", "HP"), ("hewlett", "HP"), ("hp inc", "HP"),
        ("canon", "Canon"), ("brother", "Brother"), ("epson", "Epson"), ("lexmark", "Lexmark"), ("kyocera", "Kyocera"),
        ("sonos", "Sonos"), ("philips hue", "Philips Hue"), ("shelly", "Shelly"), ("sonoff", "Sonoff"), ("tasmota", "Tasmota"),
        ("mystrom", "myStrom"), ("netatmo", "Netatmo"), ("fronius", "Fronius"), ("gardena", "Gardena"),
        // The Swisscom router comes in many spellings: "Internet-Box 5 Pro", "Internet Box 3", "InternetBox" …
        ("internet-box", "Swisscom"), ("internet box", "Swisscom"), ("internetbox", "Swisscom"), ("swisscom", "Swisscom"),
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

                // Many devices (e.g. the Swisscom Internet-Box) hop to their real UI with a
                // JS/meta redirect that HttpClient can't follow – do one hop, same host only.
                if (ClientRedirectTarget(html, hostPart, scheme) is { } jump)
                {
                    try
                    {
                        using var resp2 = await http.GetAsync(jump,
                            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        var server2 = (resp2.Headers.Server?.ToString() ?? "").Trim();
                        if (server2.Length > 0) server = server2;
                        var html2 = await ReadHtmlAsync(resp2, ct).ConfigureAwait(false);
                        if (html2.Length > 0) html = html2;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                    {
                        // keep what the first page gave us
                    }
                }

                var title = CleanTitle(ExtractTitle(html));
                if (title.Length == 0) title = CleanTitle(ExtractAltTitle(html));
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

    /// <summary>Title fallbacks for pages whose &lt;title&gt; is empty or generic:
    /// og:title / application-name metas, then the first &lt;h1&gt;.</summary>
    private static string ExtractAltTitle(string html)
    {
        var m = AltTitleRegex().Match(html);
        if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        m = H1Regex().Match(html);
        if (!m.Success) return "";
        var text = Regex.Replace(m.Groups[1].Value, "<[^>]+>", " "); // strip inner tags
        return WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>Extracts a JS location / meta-refresh redirect and returns it as an absolute URL,
    /// but only when it stays on the same host (we never wander off the device).</summary>
    private static string? ClientRedirectTarget(string html, string hostPart, string scheme)
    {
        var m = JsRedirectRegex().Match(html);
        var raw = m.Success ? m.Groups[1].Value : MetaRefreshRegex().Match(html) is { Success: true } r ? r.Groups[1].Value : "";
        if (raw.Length == 0) return null;

        if (!raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return $"{scheme}://{hostPart}/{raw.TrimStart('/')}"; // relative → same host by definition

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        var target = uri.Host.Trim('[', ']');
        var origin = hostPart.Trim('[', ']');
        return string.Equals(target, origin, StringComparison.OrdinalIgnoreCase) ? uri.ToString() : null;
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
        return title.ToLowerInvariant().TrimEnd('.', '…') is "login" or "index" or "home" or "welcome" or "anmelden"
            or "loading" or "please wait" or "redirect" or "redirecting" ? "" : title;
    }

    private static string BrandFrom(string haystack)
    {
        var lower = haystack.ToLowerInvariant();
        foreach (var (key, name) in Brands)
            if (lower.Contains(key)) return name;
        return "";
    }
}
