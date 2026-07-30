using System.Linq;
namespace TikMan.Core.Api;

/// <summary>Links into the TP-Link / Omada support site. The firmware list there is rendered by a
/// Nuxt SPA from an undocumented backend API, so instead of scraping it (which would break silently
/// on any site change) we build the public download page URL for the exact model + hardware
/// revision and open it in the browser — one click to the official firmware and its release date.</summary>
public static class OmadaSupport
{
    /// <summary>Download page for a model + hardware revision, e.g. tl-sg2008 / v3 →
    /// https://support.omadanetworks.com/us/product/tl-sg2008/v3/?resourceType=download .
    /// Returns "" when the model is unknown.</summary>
    public static string FirmwarePageUrl(string model, string hardwareRevision)
    {
        var m = Slug(model);
        if (m.Length == 0) return "";
        var rev = Slug(hardwareRevision);
        var path = rev.Length > 0 ? $"{m}/{rev}" : m;
        return $"https://support.omadanetworks.com/us/product/{path}/?resourceType=download";
    }

    /// <summary>Direct firmware download page for a known model and <b>hardware revision</b>. Both forms
    /// below are confirmed against the live site:
    /// <code>
    /// TL-SG2008, hardware 3.0   -> .../download/firmware/tl-sg2008/v3/
    /// TL-SG2008, hardware 4.60  -> .../download/firmware/tl-sg2008/v4.60/
    /// </code>
    ///
    /// <para>⚠️ The model keeps its <c>TL-</c> prefix here, exactly like the product page. And the revision
    /// is <b>not</b> simply "major.minor": a zero minor is dropped (<c>3.0</c> → <c>v3</c>) while a
    /// non-zero one is kept verbatim, digits and all (<c>4.60</c> → <c>v4.60</c>, never <c>v4.6</c>).</para>
    ///
    /// <para>⚠️ <b>Hardware</b> revision, not firmware: TP-Link organises downloads by the revision of the
    /// board, because that is what decides which image fits. A real TL-SG2008 reports
    /// <c>Hardware Version - TL-SG2008 3.0</c> and <c>Software Version - 3.0.13 Build …</c>; those two agree
    /// only by coincidence, and using the firmware would send anyone whose numbers differ to the page for
    /// the wrong board. The firmware is a last resort, for a device that reported no hardware revision.</para>
    ///
    /// <para>Returns "" when neither is usable, so the caller falls back to the product page (which needs
    /// only the model) rather than opening a link that 404s.</para></summary>
    public static string FirmwareDownloadUrl(string model, string hardwareRevision, string firmwareVersion = "")
    {
        var m = FirmwareModelSlug(model);
        // ⚠️ The version sits at opposite ends of the two strings: a hardware line is "TL-SG2008 3.0"
        // (revision LAST), a firmware line is "3.0.13 Build 20250117 Rel.60471" (version FIRST). Reading
        // either with one rule picks up a number from the model name ("…SG2008 …" → v2008) or from the
        // build date ("… 20250117" → v20250117). Both were produced by the naive version of this.
        var v = RevisionPath(VersionToken(hardwareRevision, last: true));
        if (v.Length == 0) v = RevisionPath(VersionToken(firmwareVersion, last: false));
        if (m.Length == 0 || v.Length == 0) return "";
        return $"https://support.omadanetworks.com/us/download/firmware/{m}/{v}/";
    }

    /// <summary>The first (or last) whitespace-separated token that is nothing but digits and dots – i.e. a
    /// version and not a model name or a build date glued to a word. "" when the string has none.</summary>
    private static string VersionToken(string text, bool last)
    {
        var tokens = (text ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0 && t.All(c => char.IsAsciiDigit(c) || c == '.') && t.Any(char.IsAsciiDigit))
            .ToList();
        if (tokens.Count == 0) return "";
        return last ? tokens[^1] : tokens[0];
    }

    /// <summary>A pure version token as the download path spells it: <c>3.0</c> → <c>v3</c>,
    /// <c>4.60</c> → <c>v4.60</c>, <c>3.0.13</c> → <c>v3</c>. "" when there is no version to read.
    /// <para>The minor part is compared as a NUMBER but emitted as WRITTEN – so a zero minor disappears
    /// while "60" stays "60" and is never normalised to "6".</para></summary>
    private static string RevisionPath(string version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(version ?? "", @"^(\d+)(?:\.(\d+))?");
        if (!m.Success) return "";
        var major = m.Groups[1].Value;
        if (!m.Groups[2].Success) return $"v{major}";
        var minor = m.Groups[2].Value;
        return int.TryParse(minor, out var n) && n == 0 ? $"v{major}" : $"v{major}.{minor}";
    }

    /// <summary>The model as the firmware URL wants it: <c>TL-SG2008</c> → <c>tl-sg2008</c>.
    ///
    /// <para>⚠️ The <c>TL-</c> prefix is <b>kept</b> – confirmed against the live site. Only the first token
    /// is used, so a hardware revision that rode along in the model string ("TL-SG2008 3.0") cannot leak
    /// into the slug and produce <c>tl-sg2008-3-0</c>.</para></summary>
    private static string FirmwareModelSlug(string model)
    {
        var token = (model ?? "").Trim()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Slug(token);
    }

    /// <summary>The <c>major.minor</c> version out of a string, wherever it sits: "3.0" from
    /// "TL-SG2008 3.0", "3.30" from "3.30.0 Build 20231108". "" when there is none.
    /// <para>Not anchored to the start on purpose – the hardware line carries the model in front of the
    /// number, and anchoring made it read nothing at all for exactly the input it exists for.</para></summary>
    private static string MajorMinor(string version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(version ?? "", @"(\d+)\.(\d+)");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : "";
    }

    private static string Slug(string value) =>
        new string((value ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
}
