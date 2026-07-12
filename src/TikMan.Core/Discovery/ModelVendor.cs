namespace TikMan.Core.Discovery;

/// <summary>Maps a bare device model code to its manufacturer by product-family prefix, for the
/// common case where a device only reveals its model (web title / SNMP sysDescr) and never names
/// the brand. This is path-independent (works over VPN, without a MAC) and is only meant as a
/// last-resort guess: callers check explicit brand mentions first, so a prefix here can't override
/// a real signal. Only high-confidence prefixes are listed – families shared across vendors (bare
/// "GS"/"XS"/"AP-"/"USG") are deliberately omitted so the guess never contradicts another vendor.</summary>
public static class ModelVendor
{
    // (Vendor, prefixes). Matched case-insensitively at the START of the model; the character right
    // after the prefix must be a boundary (digit / '-' / space / end), never a letter, so a product
    // code like "UAP-AC-PRO" matches but an ordinary word like "Wacom" (→ WAC) does not.
    private static readonly (string Vendor, string[] Prefixes)[] Table =
    {
        ("Zyxel",    new[] { "NWA", "WAC", "WAX", "NAP", "XGS", "XMG", "NSG", "VMG", "EMG", "SBG", "ZyWALL", "ATP" }),
        ("Ubiquiti", new[] { "UAP", "USW", "UDM", "UDR", "UXG", "UCG" }),
        ("Fortinet", new[] { "FortiGate", "FGT", "FortiSwitch", "FortiAP", "FAP-", "FSW" }),
        ("D-Link",   new[] { "DGS-", "DES-", "DIR-", "DAP-", "DXS-", "DSR-", "DWL-" }),
        ("QNAP",     new[] { "TS-", "TVS-", "TBS-", "TES-", "QGD-", "QSW-" }),
        ("Cisco",    new[] { "WS-C", "CBS", "CBW", "AIR-", "C9", "SG350", "SG250", "SG550", "SPA", "PAP" }),
        ("Aruba",    new[] { "IAP-" }),
        ("Netgear",  new[] { "Nighthawk", "Orbi", "RBR", "RBS", "GSM7" }),
    };

    /// <summary>The manufacturer inferred from a model's family prefix, or "" if none fits.</summary>
    public static string FromModel(string? model)
    {
        var m = (model ?? "").TrimStart();
        if (m.Length == 0) return "";
        foreach (var (vendor, prefixes) in Table)
            foreach (var p in prefixes)
                if (m.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                    && (m.Length == p.Length || !char.IsLetter(m[p.Length])))
                    return vendor;
        return "";
    }
}
