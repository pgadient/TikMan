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

    // Zyxel switches/APs put the exact model in <div class="login-model">XGS1930-52HP</div> on the
    // login page (title is just "Login"). Nebula-managed units keep SNMP off, so this is the only hint.
    [GeneratedRegex(@"class=[""'][^""']*\blogin-model\b[^""']*[""'][^>]*>\s*([^<]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginModelRegex();

    // Many vendors deep-link to their online help with the model in the query, e.g.
    // webhelp.zyxel.com/…?model=XGS1930-52HP&…  – a reliable model source when the title is generic.
    [GeneratedRegex(@"[?&]model=([A-Za-z0-9][A-Za-z0-9._/-]{2,30})", RegexOptions.IgnoreCase)]
    private static partial Regex HelpModelRegex();

    // Zyxel uOS firewalls (USG FLEX H series) serve a React SPA whose <title> is empty; the exact
    // model is a constant in the main JS bundle, e.g.  n="USG FLEX 500H",i="ABZH".
    [GeneratedRegex(@"src=[""'](/static/js/main\.[A-Za-z0-9]+\.chunk\.js)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex SpaMainChunkRegex();

    [GeneratedRegex(@"=\s*[""']((?:USG FLEX|ATP|USG|NSG|VPN)[0-9A-Za-z \-]{1,18})[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ZyxelModelConstRegex();

    // Prefix-free structural fallback: Zyxel bakes the model right next to its firmware-ID code,
    // e.g.  n="USG FLEX 500H",i="ABZH"  – a human string immediately followed by a short all-caps
    // code. Works for future model names the prefix list above doesn't know yet.
    [GeneratedRegex(@"=\s*[""'](?<m>[^""']{2,30})[""']\s*,\s*[A-Za-z_$]\w{0,3}\s*=\s*[""'](?<fw>[A-Z0-9]{3,8})[""']")]
    private static partial Regex ZyxelModelPairRegex();

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
        ("qts", "QNAP"), ("turbonas", "QNAP"), ("quts", "QNAP"),          // QNAP NAS web UIs
        ("topaccess", "Toshiba"), ("e-studio", "Toshiba"), ("estudio", "Toshiba"), // Toshiba MFPs
        ("microsoft-iis", "Microsoft"),                                  // IIS on Windows Server
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

                // An error page (403/404/…) is not a device title – keep the server header only.
                var metas = ExtractMetas(html);
                var title = resp.IsSuccessStatusCode ? CleanTitle(ExtractTitle(html)) : "";
                if (title.Length == 0 && resp.IsSuccessStatusCode) title = CleanTitle(ExtractAltTitle(html));
                // Model markers in the page body (Zyxel login-model div, help-link ?model=…) beat a
                // generic/empty title – they carry the real product code even behind a bland "Login".
                if (title.Length == 0 && resp.IsSuccessStatusCode && ExtractModelHint(html) is { Length: > 0 } model)
                    title = model;
                // Vendor: title/server/metas first, then vendor domains referenced on the page
                // (e.g. nebula.zyxel.com on a Nebula-managed switch whose title is just "Login").
                var vendor = BrandFrom($"{title} {server} {metas}");
                if (vendor.Length == 0) vendor = BrandFromDomains(html);
                // Some Zyxel APs/switches show only the bare model (e.g. "NWA5123-AC-HD") with no
                // "Zyxel" anywhere on the page – infer the vendor from the model's family prefix so
                // it is known even without a MAC (VPN scans).
                if (vendor.Length == 0) vendor = VendorFromModel(title);

                // Zyxel uOS firewalls hide the model in the JS bundle – fetch it once when the page
                // is that React SPA and we still have no model.
                if (title.Length == 0 && resp.IsSuccessStatusCode && LooksLikeZyxelSpa(html)
                    && await ZyxelSpaModelAsync(http, $"{scheme}://{hostPart}", html, ct).ConfigureAwait(false) is { Length: > 0 } spaModel)
                {
                    title = spaModel;
                    if (vendor.Length == 0) vendor = "Zyxel";
                }
                // Web-UI product names (TopAccess, QTS, IIS) identify the vendor but are not a model.
                if (IsUiName(title)) title = "";

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

    /// <summary>Keeps a title that looks like a device name; drops empty/very long/generic ones,
    /// HTTP error phrases ("403 Forbidden") and JavaScript-templated titles.</summary>
    private static string CleanTitle(string title)
    {
        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (title.Length is < 2 or > 80) return "";
        if (title.Contains("location.") || title.Contains("==") || title.Contains("indexOf")) return ""; // JS template
        var low = title.ToLowerInvariant();
        if (Regex.IsMatch(low, @"^\d{3}\b") ||  // "403 Forbidden", "404 Not Found", …
            low is "forbidden" or "not found" or "unauthorized" or "bad request" or "access denied"
                or "error" or "bad gateway" or "service unavailable" or "internal server error")
            return "";
        return low.TrimEnd('.', '…') is "login" or "index" or "home" or "welcome" or "anmelden"
            or "loading" or "please wait" or "redirect" or "redirecting" ? "" : title;
    }

    /// <summary>True for web-UI product names that identify the vendor but aren't a device model.</summary>
    private static bool IsUiName(string title)
    {
        var low = title.ToLowerInvariant();
        return low is "topaccess" or "qts" or "quts hero" or "turbonas" or "webfig"
            || low.Contains("iis windows server") || low.StartsWith("iis ");
    }

    private static string BrandFrom(string haystack)
    {
        var lower = haystack.ToLowerInvariant();
        foreach (var (key, name) in Brands)
            if (lower.Contains(key)) return name;
        return "";
    }

    // Distinctive Zyxel product-family prefixes (APs / switches / firewalls / gateways). Ambiguous
    // ones shared with other vendors (bare "GS"/"XS" – also Netgear) are deliberately left out.
    private static readonly string[] ZyxelModelPrefixes =
        { "NWA", "WAC", "WAX", "NAP", "XGS", "XMG", "USG", "ATP", "NSG", "VMG", "EMG", "SBG", "ZyWALL" };

    /// <summary>Infers "Zyxel" from a model code whose family prefix is unmistakably Zyxel (and that
    /// carries a digit, so it's a real product code), else "". Used only when no vendor was found.</summary>
    private static string VendorFromModel(string model) =>
        model.Length > 0 && model.Any(char.IsDigit)
        && ZyxelModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            ? "Zyxel" : "";

    /// <summary>Reads a model code from device-specific markers in the page body: the Zyxel
    /// <c>login-model</c> div and any <c>?model=…</c> help/support link. Returns "" if none.</summary>
    private static string ExtractModelHint(string html)
    {
        var m = LoginModelRegex().Match(html);
        if (m.Success)
        {
            var text = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (text.Length is >= 2 and <= 40 && Regex.IsMatch(text, @"[A-Za-z].*\d|\d.*[A-Za-z]")) return text;
        }
        m = HelpModelRegex().Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }

    private static string CleanModel(string raw) =>
        Regex.Replace(WebUtility.HtmlDecode(raw).Trim(), @"\s+", " ");

    /// <summary>True when the page is the Zyxel uOS React admin SPA (branded "ZYXEL React" template
    /// with a <c>/static/js/main.*.chunk.js</c> bundle) – its model lives in that bundle, not the DOM.</summary>
    private static bool LooksLikeZyxelSpa(string html) =>
        html.Contains("zyxel react", StringComparison.OrdinalIgnoreCase)
        && SpaMainChunkRegex().IsMatch(html);

    /// <summary>Downloads the SPA's main JS bundle once and reads the model constant baked into it,
    /// e.g. <c>n="USG FLEX 500H"</c>. Returns "" on any error or if no model constant is present.</summary>
    private static async Task<string> ZyxelSpaModelAsync(HttpClient http, string baseUrl, string html, CancellationToken ct)
    {
        var chunk = SpaMainChunkRegex().Match(html);
        if (!chunk.Success) return "";
        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/{chunk.Groups[1].Value.TrimStart('/')}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return "";
            var js = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // The model appears as an assignment (n="USG FLEX 500H"), never as a map key ("…":6),
            // so an "=…" match reliably picks this device's own model over the capabilities table.
            // 1) Known Zyxel product families – highest precision.
            var known = ZyxelModelConstRegex().Match(js);
            if (known.Success) return CleanModel(known.Groups[1].Value);
            // 2) Prefix-free fallback: a model-shaped string (letters + digits) sitting right before
            //    its firmware-ID code. Covers model names the list above doesn't know yet.
            foreach (Match pair in ZyxelModelPairRegex().Matches(js))
            {
                var cand = pair.Groups["m"].Value.Trim();
                if (cand.Any(char.IsDigit) && cand.Any(char.IsLetter)) return CleanModel(cand);
            }
            return "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>Last-resort vendor guess from manufacturer domains linked on the page (e.g. a
    /// Nebula-managed Zyxel switch links to nebula.zyxel.com / webhelp.zyxel.com). Only well-known
    /// vendor hostnames are matched, so it won't fire on generic third-party links.</summary>
    private static string BrandFromDomains(string html)
    {
        var lower = html.ToLowerInvariant();
        foreach (var (domain, name) in VendorDomains)
            if (lower.Contains(domain)) return name;
        return "";
    }

    // Manufacturer support/cloud domains that a device's own UI links to. Kept specific (full host
    // fragments) to avoid the false positives a bare brand-word scan over the whole page would cause.
    private static readonly (string Domain, string Name)[] VendorDomains =
    {
        ("zyxel.com", "Zyxel"), ("nebula.zyxel", "Zyxel"),
        ("mikrotik.com", "MikroTik"), ("tp-link.com", "TP-Link"), ("tplink", "TP-Link"),
        ("netgear.com", "Netgear"), ("synology.com", "Synology"), ("qnap.com", "QNAP"),
        ("ui.com", "Ubiquiti"), ("ubnt.com", "Ubiquiti"),
    };
}
