using System.Text;

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
        // VoIP phone makers – these ship desk phones, not servers, even when only 443 is open.
        ("yealink", DeviceKind.Phone), ("snom", DeviceKind.Phone), ("grandstream", DeviceKind.Phone),
        ("polycom", DeviceKind.Phone), ("audiocodes", DeviceKind.Phone), ("gigaset", DeviceKind.Phone),
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

    // ---- Model lines ------------------------------------------------------------------------
    // The model is the strongest signal there is: one vendor ships firewalls, switches and APs under
    // the same OUI, and a copier serves web + mail + SNMP exactly like a server would. So whenever a
    // device told us its model, that decides – before any port or vendor heuristic.

    // Vendors that essentially only ship security appliances – the maker alone settles it.
    private static readonly string[] FirewallVendors =
    {
        "fortinet", "palo alto", "sonicwall", "watchguard", "check point", "checkpoint", "sophos",
        "stormshield", "clavister", "hillstone", "forcepoint", "netgate", "endian", "untangle",
    };

    // Series names that mean "firewall" whoever built them. Matched as whole model *tokens* (see
    // Tokenize), so "USG40" → [usg, 40] hits while a stray "usg"/"atp" inside an unrelated word
    // ("STRATPOINT") does not – which a plain substring match would happily have swallowed.
    private static readonly string[] FirewallSeries =
    {
        "usg", "zywall", "atp", "nsg",                      // Zyxel (USG / USG FLEX / ATP / ZyWALL)
        "fortigate", "fortiwifi",                           // Fortinet
        "sonicwall", "firebox", "xtm",                      // SonicWall, WatchGuard
        "firepower",                                        // Cisco
        "srx",                                              // Juniper
        "pfsense", "opnsense", "ipfire", "ipcop",           // open source
        "udm", "uxg",                                       // Ubiquiti Dream Machine / gateway
        "cloudgen",                                         // Barracuda
    };

    // Short series names that only mean "firewall" for one particular maker. "XGS" is a Sophos
    // firewall but a Zyxel *switch*; "SG" a Sophos firewall but a TP-Link switch; "MX" a Meraki
    // firewall but a Juniper router. Matching them globally would misfile half the switches.
    private static readonly (string Vendor, string[] Series)[] VendorFirewallSeries =
    {
        ("sophos", new[] { "xg", "xgs", "sg", "utm" }),
        ("cisco", new[] { "asa", "ftd" }),
        ("meraki", new[] { "mx" }),
        ("palo alto", new[] { "pa" }),
        ("sonicwall", new[] { "tz", "nsa", "nssp", "soho" }),
        ("fortinet", new[] { "fg", "fgt" }),
        ("zyxel", new[] { "vpn" }),                         // Zyxel VPN50/100/300 firewalls
    };

    private static bool IsFirewall(string vendorLower, IReadOnlyList<string> modelTokens)
    {
        foreach (var t in FirewallVendors)
            if (vendorLower.Contains(t, StringComparison.Ordinal)) return true;

        foreach (var token in modelTokens)
            if (Array.IndexOf(FirewallSeries, token) >= 0) return true;

        foreach (var (vendorToken, series) in VendorFirewallSeries)
            if (vendorLower.Contains(vendorToken, StringComparison.Ordinal))
                foreach (var token in modelTokens)
                    if (Array.IndexOf(series, token) >= 0) return true;

        return false;
    }

    /// <summary>Splits a model line into lower-case tokens, breaking on non-alphanumerics *and* on
    /// letter/digit boundaries: "USG FLEX 500H" → usg, flex, 500, h and "XGS1930-52HP" → xgs, 1930,
    /// 52, hp. Whole-token matching is what keeps a short series name ("tz", "pa", "atp") from
    /// hitting inside an unrelated word.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool? wasDigit = null;

        void Flush()
        {
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
            wasDigit = null;
        }

        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch)) { Flush(); continue; }
            bool isDigit = char.IsDigit(ch);
            if (wasDigit is { } w && w != isDigit) Flush();
            wasDigit = isDigit;
            current.Append(char.ToLowerInvariant(ch));
        }
        Flush();
        return tokens;
    }

    // Printers / copiers / MFPs. A multifunction device is a printer, not a server, however many
    // services it exposes (Toshiba e-STUDIO, Brother MFC/DCP/HL, HP LaserJet, Canon imageRUNNER, …).
    private static readonly string[] PrinterModels =
    {
        "e-studio", "mfc-", "dcp-", "hl-", "laserjet", "officejet", "designjet", "imagerunner",
        "imageclass", "workcentre", "versalink", "altalink", "ecosys", "taskalfa", "aficio",
    };

    // Access points. Needed because their vendor (Zyxel, Cisco, …) otherwise reads as "switch".
    private static readonly string[] ApModels =
    {
        "nwa", "wac6", "wax",        // Zyxel
        "uap", "unifi ap",           // Ubiquiti
        "iap-", "aironet",           // Aruba, Cisco
        "eap",                       // TP-Link Omada
    };

    // IP phones and analog-telephone adapters (Yealink T-series, Cisco SPA, Grandstream GXP, …).
    private static readonly string[] PhoneModels =
    {
        "phone", "spa1", "spa5", "yealink", "snom", "grandstream", "gxp", "polycom", "vvx",
    };

    private static bool MatchesAny(string text, string[] tokens)
    {
        foreach (var t in tokens) if (text.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    // Trailing space so "…APC" at the end of a vendor name still matches the "apc " fragment.
    private static DeviceKind VendorKind(string vendorLowerWithTrailingSpace)
    {
        foreach (var (fragment, kind) in VendorHints)
            if (vendorLowerWithTrailingSpace.Contains(fragment)) return kind;
        return DeviceKind.Unknown;
    }

    /// <summary>Guesses the device kind from the model line, the open ports and the vendor – in that
    /// order of trust. Most devices on a LAN are utility gear (printers, phones, switches, APs,
    /// appliances) that happen to serve a web UI, so "Server" is only returned on real evidence, not
    /// merely because something answers on 80/443/22.</summary>
    public static DeviceKind Guess(string? vendor, IReadOnlyCollection<int> openPorts, string? model = null)
    {
        var ports = openPorts ?? Array.Empty<int>();
        bool Has(int p) => ports.Contains(p);

        var v = (vendor ?? "").ToLowerInvariant() + " ";
        var m = (model ?? "").ToLowerInvariant();

        // 1) The model line, when the device gave us one.
        if (IsFirewall(v, Tokenize(m))) return DeviceKind.Firewall;
        if (MatchesAny(m, PrinterModels)) return DeviceKind.Printer;
        if (MatchesAny(m, ApModels)) return DeviceKind.AccessPoint;
        if (MatchesAny(m, PhoneModels)) return DeviceKind.Phone;

        // 2) Services only one kind of device speaks.
        if (Has(9100) || Has(515) || Has(631)) return DeviceKind.Printer;  // JetDirect / LPD / IPP
        if (Has(5060) || Has(5061)) return DeviceKind.Phone;               // SIP
        if (Has(554) || Has(8554)) return DeviceKind.Camera;               // RTSP
        if (Has(8291) || Has(8728) || Has(8729)) return DeviceKind.Router; // MikroTik Winbox / API

        // 3) A Windows box announces itself with RDP, or with the RPC/WMI + SMB pair. That beats the
        //    OUI: a workstation on a Zyxel-branded NIC is a PC, not a switch.
        if (Has(3389) || (Has(135) && Has(445))) return DeviceKind.Pc;

        // 4) Purpose-built makers beat the port heuristics – a printer/phone/UPS/camera/NAS stays what
        //    it is even though it also serves a web UI, SNMP and scan-to-mail.
        var vendorKind = VendorKind(v);
        if (vendorKind is DeviceKind.Printer or DeviceKind.Phone or DeviceKind.Ups
            or DeviceKind.Camera or DeviceKind.Nas)
            return vendorKind;

        // 5) A real mailbox server (IMAP/POP/submission). Bare SMTP does *not* count: that is what
        //    every copier's scan-to-mail listens on, and it used to file them all as servers.
        if (Has(143) || Has(993) || Has(110) || Has(995) || Has(587) || Has(465))
            return DeviceKind.Server;

        // 6) Remaining vendor hints (router / switch / AP / PC / IoT).
        if (vendorKind != DeviceKind.Unknown) return vendorKind;

        // 7) Narrow fallbacks. Deliberately *not* "has a web or SSH port ⇒ server": most such devices
        //    are utility gear with a web UI, and calling them a server is worse than saying nothing.
        if (Has(548) || Has(2049) || Has(5000) || Has(5001)) return DeviceKind.Nas;   // AFP / NFS / DSM
        if (Has(1883) || Has(8883)) return DeviceKind.IoT;                            // MQTT broker
        if (Has(3306) || Has(5432) || Has(1433) || Has(27017) || Has(6379) ||
            Has(32400) || Has(8096) || Has(8006)) return DeviceKind.Server;          // database / media / Proxmox
        if (Has(22) && Has(445)) return DeviceKind.Server;      // a Unix host actually sharing files
        if (Has(445) || Has(139)) return DeviceKind.Pc;         // Windows / SMB host

        return DeviceKind.Unknown;
    }
}
