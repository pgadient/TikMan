using System;
using System.Text.RegularExpressions;

namespace TikMan.Core.Api;

/// <summary>A deep link to a specific firmware release's notes, when the vendor publishes them per version.
///
/// <para>MikroTik only, for now: RouterOS ships a plain-text CHANGELOG per version on its CDN
/// (<c>https://cdn.mikrotik.com/routeros/&lt;version&gt;/CHANGELOG</c>) – the exact "what changed in this
/// release" text an admin wants, and it exists for stable, long-term, testing (rc) and current development
/// (beta) builds alike. Returns "" for a vendor or version we can't build a link for, so the caller shows
/// plain text instead of a dead link.</para></summary>
public static class FirmwareChangelog
{
    /// <summary>The changelog URL for a release, or "" when none can be derived.</summary>
    public static string UrlFor(string vendor, string versionText)
    {
        if (!(vendor ?? "").Contains("MikroTik", StringComparison.OrdinalIgnoreCase)) return "";
        var v = VersionToken(versionText);
        return v.Length == 0 ? "" : $"https://cdn.mikrotik.com/routeros/{v}/CHANGELOG";
    }

    /// <summary>The bare RouterOS version out of a display string: "7.23.2 (stable) 2026-07-03" → "7.23.2",
    /// "7.24beta3 (development)" → "7.24beta3", "7.24rc3" → "7.24rc3", "6.49.18" → "6.49.18". "" when the
    /// string carries no version.
    /// <para>The pre-release suffix (beta/rc/alpha + number) is kept attached, because the CDN path spells it
    /// that way ("7.24beta3"), and it must not swallow a following date's digits. Pure, so the smoke test can
    /// pin it against real version strings.</para></summary>
    public static string VersionToken(string text)
    {
        var m = Regex.Match(text ?? "",
            @"\b(\d+\.\d+(?:\.\d+)?(?:(?:beta|rc|alpha)\d+)?)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }
}
