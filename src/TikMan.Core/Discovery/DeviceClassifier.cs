namespace TikMan.Core.Discovery;

/// <summary>Rough kind of a discovered device, guessed from its vendor (OUI) and open ports.
/// Purely heuristic — meant as a hint in the list, never authoritative.</summary>
public enum DeviceKind
{
    Unknown,
    Router,
    Firewall,
    Switch,
    AccessPoint,
    Printer,
    Nas,
    Pc,
    Phone,
    Camera,
    IoT,
    Server,
    Ups,
    Laptop,
    Notebook,
    Tablet,
}

/// <summary>Best-effort classification of a device into a <see cref="DeviceKind"/>.</summary>
public static class DeviceClassifier
{
    // Vendor name fragments (lower-case) → kind. Checked as substrings, first match wins.
    private static readonly (string Fragment, DeviceKind Kind)[] VendorHints =
    {
        ("mikrotik", DeviceKind.Router), ("routerboard", DeviceKind.Router),
        // APC / Schneider gear on the network is almost always a UPS management card.
        ("american power", DeviceKind.Ups), ("apc ", DeviceKind.Ups), ("schneider electric", DeviceKind.Ups),
        ("eaton", DeviceKind.Ups), ("cyberpower", DeviceKind.Ups),
        ("synology", DeviceKind.Nas), ("qnap", DeviceKind.Nas), ("western digital", DeviceKind.Nas),
        ("buffalo", DeviceKind.Nas), ("terra master", DeviceKind.Nas),
        ("hewlett", DeviceKind.Printer), ("hp inc", DeviceKind.Printer), ("canon", DeviceKind.Printer),
        ("brother", DeviceKind.Printer), ("epson", DeviceKind.Printer), ("lexmark", DeviceKind.Printer),
        ("kyocera", DeviceKind.Printer), ("xerox", DeviceKind.Printer), ("ricoh", DeviceKind.Printer),
        ("hikvision", DeviceKind.Camera), ("dahua", DeviceKind.Camera), ("axis comm", DeviceKind.Camera),
        ("reolink", DeviceKind.Camera), ("mobotix", DeviceKind.Camera),
        ("espressif", DeviceKind.IoT), ("tuya", DeviceKind.IoT), ("sonoff", DeviceKind.IoT),
        ("shelly", DeviceKind.IoT), ("sonos", DeviceKind.IoT), ("nest", DeviceKind.IoT),
        ("gardena", DeviceKind.IoT), ("mystrom", DeviceKind.IoT), ("netatmo", DeviceKind.IoT),
        ("tasmota", DeviceKind.IoT), ("teufel", DeviceKind.IoT), ("frontier silicon", DeviceKind.IoT),
        ("amazon tech", DeviceKind.IoT), ("google", DeviceKind.IoT), ("signify", DeviceKind.IoT),
        ("philips lighting", DeviceKind.IoT), ("tp-link", DeviceKind.Switch),
        ("cisco", DeviceKind.Switch), ("juniper", DeviceKind.Switch), ("netgear", DeviceKind.Switch),
        ("zyxel", DeviceKind.Switch), ("d-link", DeviceKind.Switch), ("aruba", DeviceKind.AccessPoint),
        ("ubiquiti", DeviceKind.AccessPoint), ("apple", DeviceKind.Pc), ("dell", DeviceKind.Pc),
        ("lenovo", DeviceKind.Pc), ("micro-star", DeviceKind.Pc), ("asustek", DeviceKind.Pc),
        ("gigabyte", DeviceKind.Pc), ("intel", DeviceKind.Pc), ("samsung", DeviceKind.Pc),
        ("raspberry", DeviceKind.Pc), // a bare Pi is a small computer; a running service above wins
    };

    // Firewall / security-appliance model lines. These are more telling than the OUI, since a Zyxel
    // firewall and a Zyxel switch share the same vendor. Kept specific so switch models (Zyxel's
    // XGS/GS lines) never match. Zyxel: USG / USG FLEX / ATP / ZyWALL; others by their series name.
    private static readonly string[] FirewallModelTokens =
    {
        "usg", "atp", "zywall",                                  // Zyxel
        "fortigate", "fortiwifi",                                // Fortinet
        "sonicwall", "firebox", "firepower",                    // SonicWall, WatchGuard, Cisco
        "pfsense", "opnsense", "netgate", "srx",                // *sense / netgate / Juniper SRX
    };

    // Vendors that essentially only make firewalls, so the maker alone is enough.
    private static readonly string[] FirewallVendorTokens =
    {
        "fortinet", "palo alto", "sonicwall", "watchguard", "check point", "checkpoint", "sophos",
    };

    private static bool IsFirewall(string vendorLower, string modelLower)
    {
        foreach (var t in FirewallVendorTokens) if (vendorLower.Contains(t)) return true;
        foreach (var t in FirewallModelTokens) if (modelLower.Contains(t)) return true;
        return false;
    }

    // Trailing space so "…APC" at the end of a vendor name still matches the "apc " fragment.
    private static DeviceKind VendorKind(string vendorLowerWithTrailingSpace)
    {
        foreach (var (fragment, kind) in VendorHints)
            if (vendorLowerWithTrailingSpace.Contains(fragment)) return kind;
        return DeviceKind.Unknown;
    }

    /// <summary>Guesses the device kind from vendor, open ports and (optionally) the model line.
    /// Definitive services and purpose-built makers (printer/firewall/UPS/camera/NAS) win over the
    /// generic "runs a web/SSH port ⇒ server" heuristic.</summary>
    public static DeviceKind Guess(string? vendor, IReadOnlyCollection<int> openPorts, string? model = null)
    {
        var ports = openPorts ?? Array.Empty<int>();
        bool Has(int p) => ports.Contains(p);

        var v = (vendor ?? "").ToLowerInvariant() + " ";
        var m = (model ?? "").ToLowerInvariant();

        // A firewall, recognised by its model line (or a firewall-only vendor). Checked first so a
        // security appliance that also serves a web UI isn't mistaken for a plain server.
        if (IsFirewall(v, m)) return DeviceKind.Firewall;

        // Definitive service signals – a device speaking these *is* that kind, whoever made it.
        if (Has(9100) || Has(515) || Has(631)) return DeviceKind.Printer;  // JetDirect / LPD / IPP
        if (Has(554) || Has(8554)) return DeviceKind.Camera;               // RTSP
        if (Has(5060) || Has(5061)) return DeviceKind.Phone;               // SIP
        if (Has(8291) || Has(8728) || Has(8729)) return DeviceKind.Router; // MikroTik Winbox / API

        // Purpose-built hardware wins over generic service ports: a printer/UPS/camera/NAS is that
        // kind even when it also exposes a web/SSH/mail port. Printers in particular often run an
        // embedded web + mail-relay stack that would otherwise read as a server.
        var vendorKind = VendorKind(v);
        if (vendorKind is DeviceKind.Printer or DeviceKind.Ups or DeviceKind.Camera or DeviceKind.Nas)
            return vendorKind;

        // A mail stack (SMTP/IMAP/POP3/submission) means a server, whoever made the board.
        if (Has(25) || Has(587) || Has(465) || Has(143) || Has(993) || Has(110) || Has(995))
            return DeviceKind.Server;

        // Remaining vendor hints (router/switch/AP/PC/IoT).
        if (vendorKind != DeviceKind.Unknown) return vendorKind;

        // Broader service fallbacks – only reached when the vendor gave nothing away, but a running
        // service is still a decent hint on its own.
        if (Has(548) || Has(2049) || Has(5000) || Has(5001)) return DeviceKind.Nas;   // AFP / NFS / Synology DSM
        if (Has(1883) || Has(8883)) return DeviceKind.IoT;                            // MQTT broker
        if (Has(3306) || Has(5432) || Has(1433) || Has(27017) || Has(6379) ||
            Has(32400) || Has(8096) || Has(8006)) return DeviceKind.Server;          // database / media / Proxmox
        if (Has(3389)) return DeviceKind.Pc;                     // RDP → a Windows box
        if (Has(445) || Has(139)) return DeviceKind.Pc;         // Windows / SMB host
        if (Has(22) || Has(80) || Has(443)) return DeviceKind.Server; // an SSH / web-facing box

        return DeviceKind.Unknown;
    }
}
