namespace TikMan.Core.Api;

/// <summary>Links into the TP-Link / Omada support site. The firmware list there is rendered by a
/// Nuxt SPA from an undocumented backend API, so instead of scraping it (which would break silently
/// on any site change) we build the public download page URL for the exact model + hardware
/// revision and open it in the browser — one click to the official firmware and its release date.</summary>
public static class OmadaSupport
{
    /// <summary>Download page for a model + hardware revision, e.g. tl-sg2008 / v3 →
    /// https://support.omadanetworks.com/en/product/tl-sg2008/v3/?resourceType=download .
    /// Returns "" when the model is unknown.</summary>
    public static string FirmwarePageUrl(string model, string hardwareRevision)
    {
        var m = Slug(model);
        if (m.Length == 0) return "";
        var rev = Slug(hardwareRevision);
        var path = rev.Length > 0 ? $"{m}/{rev}" : m;
        return $"https://support.omadanetworks.com/en/product/{path}/?resourceType=download";
    }

    private static string Slug(string value) =>
        new string((value ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
}
