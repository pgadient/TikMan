namespace TikMan.Core.Api;

/// <summary>Where to send the user for a device's official firmware.
///
/// <para>One place, shared by both clients, because the useful answer differs per vendor: TP-Link has a
/// per-model download page whose URL can be built from the model and hardware revision, MikroTik publishes
/// one changelog page for all of RouterOS, and most other vendors bury firmware behind a search that cannot
/// be guessed. ⚠️ Returning "" for those is deliberate – a fabricated link that 404s is worse than an
/// honest "no page known for &lt;vendor&gt;", because the user cannot tell the two apart from a browser tab.</para></summary>
public static class FirmwarePages
{
    /// <summary>The RouterOS changelog: the release notes for every version, which is the page a MikroTik
    /// admin actually wants (the download itself happens in the device, through the update channel).</summary>
    public const string MikroTikChangelog = "https://mikrotik.com/download/changelogs";

    /// <summary>The Omada download hub, for a TP-Link/Omada device whose exact model is unknown – still one
    /// step closer than nothing. Same site as the per-model page (<see cref="OmadaSupport"/>), so the user
    /// stays in the place where the model page would have taken them.</summary>
    public const string TpLinkDownloads = "https://support.omadanetworks.com/us/download/";

    /// <summary>Zyxel's Download Library. The switch's model goes in as the <c>?model=</c> query, which the
    /// site uses to pre-fill its "search by model number" box, so the user lands on that model's firmware
    /// (with its release note and date) rather than a blank search.</summary>
    public const string ZyxelDownloads = "https://www.zyxel.com/global/en/support/download";

    /// <summary>Ubiquiti's official download portal (model-agnostic hub, verified 200). Used when the model is
    /// unknown; with a model, <see cref="UrlFor"/> builds the per-model page under it. UniFi firmware is not
    /// installed from TikMan; this is the "open the download page" link.</summary>
    public const string UbiquitiDownloads = "https://ui.com/download";

    /// <summary>The per-model UniFi download page, e.g. <c>…/download/software/usw-lite-16-poe</c>. The slug is
    /// the model lower-cased with non-alphanumerics folded to single hyphens (the site's own scheme, verified
    /// 200 for USW-Lite-16-PoE). "" when no model, so the caller uses the hub.</summary>
    public static string UbiquitiModelPage(string model)
    {
        var slug = ModelSlug(model);
        return slug.Length > 0 ? $"{UbiquitiDownloads}/software/{slug}" : "";
    }

    /// <summary>Schneider Electric's product page for the APC Network Management Cards range (61936) – its
    /// "Software &amp; Firmware" section is where the NMC firmware lives. Opened in the user's real browser,
    /// which is the only client that gets past the page's Akamai bot protection.</summary>
    public const string ApcNmcPage = "https://www.se.com/us/en/product-range/61936-network-management-cards/";

    private static string ModelSlug(string model)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in (model ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');   // fold runs of separators to one hyphen
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>The firmware page for a device, or "" when the vendor has no page that can be derived.
    ///
    /// <para>For TP-Link/Omada, most specific first: model + firmware version go straight to the firmware
    /// download page; model alone (with any hardware revision) to the product page; nothing to the Omada
    /// download hub.</para></summary>
    /// <param name="vendor">The <b>identified</b> vendor (what the device said about itself), never
    /// <c>Device.Vendor</c> – that enum is the connector kind, defaults to MikroTik and is never assigned.</param>
    public static string UrlFor(string vendor, string model, string hardwareRevision = "",
        string firmwareVersion = "")
    {
        vendor = (vendor ?? "").Trim();
        model = (model ?? "").Trim();

        if (vendor.Contains("MikroTik", StringComparison.OrdinalIgnoreCase))
            return MikroTikChangelog;

        if (vendor.Contains("TP-Link", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("TPLink", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("Omada", StringComparison.OrdinalIgnoreCase))
        {
            if (model.Length == 0) return TpLinkDownloads;
            // Prefer the direct firmware page when the board revision is known; fall back to the product
            // page, which needs only the model.
            var direct = OmadaSupport.FirmwareDownloadUrl(model, hardwareRevision, firmwareVersion);
            if (direct.Length > 0) return direct;
            var product = OmadaSupport.FirmwarePageUrl(model, hardwareRevision);
            return product.Length > 0 ? product : TpLinkDownloads;
        }

        if (vendor.Contains("Ubiquiti", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("UniFi", StringComparison.OrdinalIgnoreCase))
        {
            // Per-model page when the model is known (…/software/<slug>), else the download hub.
            var page = UbiquitiModelPage(model);
            return page.Length > 0 ? page : UbiquitiDownloads;
        }

        if (vendor.Contains("APC", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("American Power", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("Schneider", StringComparison.OrdinalIgnoreCase))
            return ApcNmcPage;

        if (vendor.Contains("Zyxel", StringComparison.OrdinalIgnoreCase))
        {
            // ⚠️ No derivable per-model DEEP link: Zyxel's direct DownloadLandingSR URL needs an internal
            // per-model kbid (e.g. M-01903) that cannot be computed from the model. The Download Library
            // itself, however, takes a ?model= query that pre-fills its search – so pass the model when we
            // have it (read off the switch once a login is set) and open the plain library otherwise. Both
            // are real pages, so neither 404s.
            if (model.Length == 0) return ZyxelDownloads;
            // First whitespace token only, so a model that arrived with trailing text ("GS1920-48 (…)")
            // doesn't push junk into the query.
            var parts = model.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            var m = parts.Length > 0 ? parts[0] : model;
            return $"{ZyxelDownloads}?model={System.Uri.EscapeDataString(m)}";
        }

        return "";
    }
}
