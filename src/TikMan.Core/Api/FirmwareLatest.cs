using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TikMan.Core.Api;

/// <summary>The newest firmware version a vendor's public download page advertises, plus the page it was
/// read from. For vendors without a real update API (everyone but MikroTik), this is the honest substitute:
/// fetch the official download page and read the top version off it.
///
/// <para><see cref="Parsed"/> says whether a version could actually be read. When it could not – the page
/// renders its list client-side, or the model is end-of-life and its files were pulled – <see cref="Version"/>
/// is empty and the caller shows a "manual search" link to <see cref="SourceUrl"/> instead of a fabricated
/// number. That fallback is deliberate: a wrong version is worse than an honest "look it up here".</para></summary>
public sealed record LatestFirmware(string Version, string SourceUrl, bool Parsed);

/// <summary>Reads the latest published firmware version for a device off its vendor's download page.
/// <para>⚠️ This is best-effort web scraping, and it is meant to be: these vendors publish no version API, so
/// the alternative is nothing. It is written to fail SOFT – any fetch error, any layout change that stops the
/// pattern matching, and it returns <c>Parsed = false</c> with the download page as the fallback link, never
/// an exception and never a guessed number.</para></summary>
public static class FirmwareLatest
{
    // One shared client: a short timeout (a stale update column is fine, a hung one is not) and a normal
    // browser UA (some vendor CDNs answer bots with a stub page).
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return c;
    }

    /// <summary>Whether this vendor is one we even try – so the caller can skip the network round trip for
    /// devices that would only ever return nothing.</summary>
    public static bool Supports(string vendor)
    {
        vendor = vendor ?? "";
        return vendor.Contains("MikroTik", StringComparison.OrdinalIgnoreCase)   // handled elsewhere, but "supported"
            || IsTpLink(vendor) || vendor.Contains("Zyxel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vendors whose latest firmware we read from the web (no device login needed) – as opposed to
    /// MikroTik, which is checked through its own update API. The caller uses this to decide which devices to
    /// include in a "check for latest" run.</summary>
    public static bool IsWebVendor(string vendor) =>
        IsTpLink(vendor ?? "") || (vendor ?? "").Contains("Zyxel", StringComparison.OrdinalIgnoreCase);

    private static bool IsTpLink(string vendor) =>
        vendor.Contains("TP-Link", StringComparison.OrdinalIgnoreCase)
        || vendor.Contains("TPLink", StringComparison.OrdinalIgnoreCase)
        || vendor.Contains("Omada", StringComparison.OrdinalIgnoreCase);

    /// <summary>The latest firmware for a device, or null when the vendor isn't one we scrape. A non-null
    /// result always carries a usable <see cref="LatestFirmware.SourceUrl"/> (the download page), even when
    /// <see cref="LatestFirmware.Parsed"/> is false – that is the "manual search" link.</summary>
    public static async Task<LatestFirmware?> QueryAsync(string vendor, string model, string hardwareRevision,
        string firmwareVersion, CancellationToken ct = default)
    {
        vendor = (vendor ?? "").Trim();
        model = (model ?? "").Trim();

        // The download page URL is FirmwarePages' business – reuse it so the fallback link is always the same
        // place "Open firmware page" would take the user.
        var url = FirmwarePages.UrlFor(vendor, model, hardwareRevision, firmwareVersion);
        if (url.Length == 0) return null;              // vendor we don't have a page for ⇒ nothing to show

        string html;
        try { html = await Http.GetStringAsync(url, ct).ConfigureAwait(false); }
        catch { return new LatestFirmware("", url, false); }   // offline / blocked ⇒ manual search

        var version = ParseVersion(vendor, html);

        return version.Length > 0
            ? new LatestFirmware(version, url, true)
            : new LatestFirmware("", url, false);      // client-rendered / EOL ⇒ manual search
    }

    /// <summary>Whether the freshly-read <paramref name="latest"/> is actually newer than what the device
    /// runs. Compared as version <b>tokens</b> (first <c>d.d[.d…]</c> in each), because the installed string
    /// carries extra text a raw equality would trip on ("3.0.13 Build 20250117" vs "3.0.13"). Unknown ⇒
    /// false: an update column that cries wolf is worse than a quiet one.</summary>
    public static bool IsNewer(string latest, string installed)
    {
        var l = VersionToken(latest);
        var i = VersionToken(installed);
        if (l.Length == 0 || i.Length == 0) return false;
        return !l.Equals(i, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the newest version out of a vendor's download-page HTML. Public and pure so it can be
    /// pinned against captured markup without a network round trip. "" when nothing matches.</summary>
    public static string ParseVersion(string vendor, string html)
    {
        vendor = vendor ?? "";
        if (IsTpLink(vendor)) return ParseTpLink(html ?? "");
        if (vendor.Contains("Zyxel", StringComparison.OrdinalIgnoreCase)) return ParseZyxel(html ?? "");
        return "";
    }

    // TP-Link/Omada: the download page lists the newest firmware first, as a filename like
    // "TL-SG2008(UN)_V3_3.0.13 Build 20250117". The version sits right after the "_V<rev>_" segment. Server-
    // rendered, so a plain GET sees it (verified against the live site). First match = latest.
    private static string ParseTpLink(string html)
    {
        var m = Regex.Match(html, @"_[Vv]\d[\d.]*_[vV]?(\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        if (m.Success) return m.Groups[1].Value;
        // Fallback shape: "3.0.13 Build 20250117" anywhere.
        m = Regex.Match(html, @"(\d+\.\d+(?:\.\d+)?)\s+Build\s+\d{6,}", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : "";
    }

    // Zyxel: a firmware code looks like "5.00(ABHT.0)C0". ⚠️ In practice the Download Library renders its
    // list CLIENT-SIDE, so a plain GET usually finds nothing here and the caller falls back to manual search
    // (which is fine – the user asked for exactly that). Kept anyway: if a model's page is ever server-
    // rendered (or Zyxel changes that), this reads it for free. First match = newest (listed first).
    private static string ParseZyxel(string html)
    {
        var m = Regex.Match(html, @"\b(\d\.\d{2}\([A-Z]{3,5}\.\d+\)[A-Za-z0-9]*)", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string VersionToken(string s)
    {
        var m = Regex.Match(s ?? "", @"\d+(?:\.\d+)+", RegexOptions.CultureInvariant);
        return m.Success ? m.Value : "";
    }
}
