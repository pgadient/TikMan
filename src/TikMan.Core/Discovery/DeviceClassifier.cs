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
    /// <summary>Card payment terminal (Banksys/Worldline, Ingenico, Verifone, …).</summary>
    PaymentTerminal,
    /// <summary>Franking machine / postage meter (Francotyp-Postalia, Pitney Bowes, Neopost, …).</summary>
    Franking,
    /// <summary>Out-of-band management controller (BMC) or an IP-KVM: Fujitsu iRMC, HPE iLO, Dell
    /// iDRAC, IPMI, Intel AMT/vPro, JetKVM. A separate little computer, not the host it manages.</summary>
    Management,
    /// <summary>Smartphone (iPhone, Android handset) – distinct from <see cref="Phone"/>, which is a
    /// VoIP desk phone.</summary>
    Smartphone,
    /// <summary>Speaker / audio streamer (Sonos, Teufel, Bose, HomePod, internet radio).</summary>
    Audio,
    /// <summary>Games console (PlayStation, Xbox, Nintendo).</summary>
    GameConsole,
    /// <summary>TV or set-top / streaming box (Philips/Sony TV, Apple TV, Swisscom TV Box, Roku).</summary>
    Tv,
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
        // Card payment terminals – single-purpose boxes that happen to serve a web/SSH port.
        ("banksys", DeviceKind.PaymentTerminal), ("worldline", DeviceKind.PaymentTerminal),
        ("ingenico", DeviceKind.PaymentTerminal), ("verifone", DeviceKind.PaymentTerminal),
        ("castles technology", DeviceKind.PaymentTerminal), ("pax computer", DeviceKind.PaymentTerminal),
        ("sumup", DeviceKind.PaymentTerminal), ("hypercom", DeviceKind.PaymentTerminal),
        // Franking machines / postage meters.
        ("francotyp", DeviceKind.Franking), ("pitney bowes", DeviceKind.Franking),
        ("neopost", DeviceKind.Franking), ("quadient", DeviceKind.Franking), ("frama", DeviceKind.Franking),
        // BMC chips: an ASPEED/Nuvoton NIC *is* the management controller, never the host.
        ("aspeed", DeviceKind.Management),
        ("espressif", DeviceKind.IoT), ("tuya", DeviceKind.IoT), ("sonoff", DeviceKind.IoT),
        ("shelly", DeviceKind.IoT), ("nest", DeviceKind.IoT),
        ("gardena", DeviceKind.IoT), ("husqvarna", DeviceKind.IoT), ("mystrom", DeviceKind.IoT),
        ("netatmo", DeviceKind.IoT), ("tasmota", DeviceKind.IoT),
        ("amazon tech", DeviceKind.IoT), ("google", DeviceKind.IoT), ("signify", DeviceKind.IoT),
        ("philips lighting", DeviceKind.IoT),
        // Smart-home hubs & gateways: heating, pets, blinds, radio hubs.
        ("viessmann", DeviceKind.IoT), ("xavi", DeviceKind.IoT),          // VitoConnect uses XAVi's OUI
        ("sure petcare", DeviceKind.IoT), ("eq-3", DeviceKind.IoT),       // Homematic
        ("dexatek", DeviceKind.IoT), ("elgato", DeviceKind.IoT),
        ("somfy", DeviceKind.IoT), ("shenzhen bilian", DeviceKind.IoT),
        // Speakers / audio streamers – these makers do sound and nothing else.
        ("teufel", DeviceKind.Audio), ("sonos", DeviceKind.Audio), ("bose", DeviceKind.Audio),
        ("frontier silicon", DeviceKind.Audio),                            // internet-radio modules
        ("denon", DeviceKind.Audio), ("marantz", DeviceKind.Audio), ("yamaha", DeviceKind.Audio),
        ("harman", DeviceKind.Audio), ("libratone", DeviceKind.Audio), ("devialet", DeviceKind.Audio),
        ("bang & olufsen", DeviceKind.Audio), ("bluesound", DeviceKind.Audio), ("sonance", DeviceKind.Audio),
        // Games consoles.
        ("nintendo", DeviceKind.GameConsole), ("sony interactive", DeviceKind.GameConsole),
        ("valve corp", DeviceKind.GameConsole),
        // TVs and set-top / streaming boxes.
        ("tp vision", DeviceKind.Tv),                                      // Philips TVs
        ("vestel", DeviceKind.Tv), ("roku", DeviceKind.Tv), ("technicolor", DeviceKind.Tv),
        ("skyworth", DeviceKind.Tv), ("hisense", DeviceKind.Tv), ("tcl ", DeviceKind.Tv),
        // IP-KVM / remote console – out-of-band management, same family as a BMC.
        ("buildjet", DeviceKind.Management), ("raritan", DeviceKind.Management),
        ("avocent", DeviceKind.Management), ("tp-link", DeviceKind.Switch),
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

    // Out-of-band management controllers. Matched as whole tokens – "ilo" and "imm" are far too short
    // to go hunting for as substrings ("silo", "immersion", …).
    private static readonly string[] ManagementSeries =
    {
        "irmc",                  // Fujitsu
        "ilo",                   // HPE
        "idrac",                 // Dell
        "imm", "xcc",            // Lenovo / IBM
        "cimc",                  // Cisco UCS
        "megarac",               // AMI (the BMC firmware behind most white-box boards)
        "jetkvm",                // BuildJet IP-KVM
        "ipmi", "bmc", "amt", "vpro",
    };

    // Consumer routers / ISP gateways that aren't MikroTik. Substring-matched: "IB5-P-00" is the
    // Swisscom Internet Box 5, and tokenising would split the "ib" from the "5".
    private static readonly string[] RouterModels =
    {
        "internet box", "ib5", "ib4", "ib3", "fritz!box", "fritzbox", "speedport", "easybox",
    };

    // TVs and set-top / streaming boxes. Mostly reached via UPnP, which is the only thing that names
    // them: a Swisscom TV Box, a smart TV and a Chromecast all sit on generic ODM OUIs (Arcadyan,
    // Vestel …) behind a bare web port, so neither MAC nor ports can place them – but they announce
    // themselves over SSDP as a MediaRenderer with a friendly name.
    private static readonly string[] TvModels =
    {
        "tv box", "tvbox", "mediarenderer", "smart tv", "set-top", "settop", "iptv",
        "bravia", "viera", "aquos", "chromecast", "shield tv", "fire tv", "apple tv", "android tv",
    };

    // What a device calls itself in DHCP/DNS. Often the only thing that can tell an iPhone from an
    // iPad from a HomePod – they all share one Apple OUI – and the *only* signal at all for the many
    // phones and laptops that randomise their MAC and therefore have no vendor to look up.
    private static readonly (string Fragment, DeviceKind Kind)[] HostHints =
    {
        ("iphone", DeviceKind.Smartphone), ("android", DeviceKind.Smartphone),
        ("galaxy", DeviceKind.Smartphone), ("pixel", DeviceKind.Smartphone),
        ("oneplus", DeviceKind.Smartphone), ("xiaomi", DeviceKind.Smartphone),
        ("redmi", DeviceKind.Smartphone),
        ("ipad", DeviceKind.Tablet), ("tablet", DeviceKind.Tablet), ("-tab-", DeviceKind.Tablet),
        ("macbook", DeviceKind.Pc), ("imac", DeviceKind.Pc), ("macmini", DeviceKind.Pc),
        ("mac-mini", DeviceKind.Pc), ("macpro", DeviceKind.Pc),
        ("xbox", DeviceKind.GameConsole), ("playstation", DeviceKind.GameConsole),
        ("ps5", DeviceKind.GameConsole), ("ps4", DeviceKind.GameConsole),
        ("appletv", DeviceKind.Tv), ("apple-tv", DeviceKind.Tv),
        ("homepod", DeviceKind.Audio),
        ("jetkvm", DeviceKind.Management),
    };

    private static DeviceKind HostKind(string hostLower)
    {
        if (hostLower.Length == 0) return DeviceKind.Unknown;
        foreach (var (fragment, kind) in HostHints)
            if (hostLower.Contains(fragment, StringComparison.Ordinal)) return kind;
        return DeviceKind.Unknown;
    }

    // A MikroTik board code spells out its radios: band (2 or 5), optional H/HP power, then the
    // standard (n / ac / ax) – "5HPaxD2HPaxD", "5HacD", "2axD", "2HnD". No radio code, no wireless.
    private static readonly System.Text.RegularExpressions.Regex WirelessBoard =
        new(@"[25]h?p?(n|ac|ax)d?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Splits MikroTik's line into switch / access point / router. RouterOS is the same on
    /// every box and any of them can be configured as anything, so the ports and the OS say nothing –
    /// only the board code does. CRS/CSS are Cloud Router *Switches*; a board carrying a wireless
    /// radio is an access point; everything else (CCR, RB, L009, hEX) is a router.</summary>
    public static DeviceKind MikroTikKind(string? board)
    {
        var m = (board ?? "").ToLowerInvariant();
        if (m.StartsWith("crs", StringComparison.Ordinal) || m.StartsWith("css", StringComparison.Ordinal))
            return DeviceKind.Switch;
        if (WirelessBoard.IsMatch(m)) return DeviceKind.AccessPoint;
        return DeviceKind.Router;
    }

    /// <summary>What a device said about itself over mDNS. This outranks everything else, because it
    /// is the device's own answer rather than our inference: an iPhone, an iPad, a HomePod and an
    /// Apple TV share one OUI and one (empty) port list, and only here do they differ –
    /// "iPhone15,2" / "iPad13,8" / "AudioAccessory5,1" / "AppleTV6,2". Unknown when mDNS said nothing
    /// that places the device.</summary>
    public static DeviceKind MdnsKind(string? model, IEnumerable<string>? services)
    {
        var m = (model ?? "").ToLowerInvariant();

        // Apple states its hardware model outright.
        if (m.StartsWith("iphone", StringComparison.Ordinal)) return DeviceKind.Smartphone;
        if (m.StartsWith("ipad", StringComparison.Ordinal)) return DeviceKind.Tablet;
        if (m.StartsWith("audioaccessory", StringComparison.Ordinal)) return DeviceKind.Audio;   // HomePod
        if (m.StartsWith("appletv", StringComparison.Ordinal)) return DeviceKind.Tv;
        if (m.StartsWith("macbook", StringComparison.Ordinal) || m.StartsWith("imac", StringComparison.Ordinal) ||
            m.StartsWith("macmini", StringComparison.Ordinal) || m.StartsWith("macpro", StringComparison.Ordinal) ||
            m.StartsWith("mac", StringComparison.Ordinal)) return DeviceKind.Pc;

        var svc = new HashSet<string>(services ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        bool Any(params string[] names) => names.Any(svc.Contains);

        // Then the services. A printer that speaks IPP is a printer, whatever its badge says.
        if (Any("_ipp._tcp", "_printer._tcp", "_pdl-datastream._tcp", "_scanner._tcp")) return DeviceKind.Printer;
        if (Any("_googlecast._tcp")) return DeviceKind.Tv;                    // Chromecast / Android TV
        if (Any("_sonos._tcp", "_spotify-connect._tcp")) return DeviceKind.Audio;
        // AirPlay *audio* (RAOP) without AirPlay video is a speaker; with video it's a TV box.
        if (Any("_raop._tcp") && !Any("_airplay._tcp")) return DeviceKind.Audio;
        // A HomePod and an Apple TV both do AirPlay, and when Apple publishes only a board id
        // ("B520AP", "J305AP") the model can't separate them either. This does: a HomePod is itself a
        // HomeKit *accessory* and advertises _hap, while an Apple TV is the HomeKit *hub* and doesn't.
        if (Any("_airplay._tcp") && Any("_hap._tcp")) return DeviceKind.Audio;
        if (Any("_airplay._tcp")) return DeviceKind.Tv;
        if (Any("_adisk._tcp", "_afpovertcp._tcp")) return DeviceKind.Nas;    // Time Machine / AFP share
        if (Any("_hap._tcp")) return DeviceKind.IoT;                          // HomeKit accessory
        if (Any("_workstation._tcp")) return DeviceKind.Pc;

        return DeviceKind.Unknown;
    }

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
    public static DeviceKind Guess(string? vendor, IReadOnlyCollection<int> openPorts, string? model = null,
        string? hostname = null)
    {
        var ports = openPorts ?? Array.Empty<int>();
        bool Has(int p) => ports.Contains(p);

        var v = (vendor ?? "").ToLowerInvariant() + " ";
        var m = (model ?? "").ToLowerInvariant();

        // 1) The model line, when the device gave us one.
        var tokens = Tokenize(m);
        if (IsFirewall(v, tokens)) return DeviceKind.Firewall;
        foreach (var t in tokens)
            if (Array.IndexOf(ManagementSeries, t) >= 0) return DeviceKind.Management;
        if (MatchesAny(m, PrinterModels)) return DeviceKind.Printer;
        if (MatchesAny(m, ApModels)) return DeviceKind.AccessPoint;
        if (MatchesAny(m, PhoneModels)) return DeviceKind.Phone;
        if (MatchesAny(m, RouterModels)) return DeviceKind.Router;
        if (MatchesAny(m, TvModels)) return DeviceKind.Tv;

        // 2) Services only one kind of device speaks.
        if (Has(9100) || Has(515) || Has(631)) return DeviceKind.Printer;  // JetDirect / LPD / IPP
        if (Has(623) || Has(16992) || Has(16993)) return DeviceKind.Management; // IPMI / Intel AMT
        if (Has(554) || Has(8554)) return DeviceKind.Camera;               // RTSP
        if (Has(8291) || Has(8728) || Has(8729)) return DeviceKind.Router; // MikroTik Winbox / API

        // SIP: a desk phone speaks SIP and little else. A PBX (3CX, Asterisk, FreePBX) speaks SIP *and*
        // is a general-purpose host – it has SSH or RDP. Phones don't. So SIP + a shell ⇒ telephony
        // server, not a handset. (A real phone whose maker or model we know was already caught above.)
        if (Has(5060) || Has(5061))
            return Has(22) || Has(3389) ? DeviceKind.Server : DeviceKind.Phone;

        // 3) A Windows box announces itself with RDP, or with the RPC/WMI + SMB pair. That beats the
        //    OUI: a workstation on a Zyxel-branded NIC is a PC, not a switch.
        if (Has(3389) || (Has(135) && Has(445))) return DeviceKind.Pc;

        // 4) What the device calls itself. This outranks the vendor, because the vendor cannot help
        //    here: an iPhone, an iPad, a HomePod and an Apple TV are one and the same OUI, and a
        //    phone with a randomised MAC has no OUI at all.
        var hostKind = HostKind((hostname ?? "").ToLowerInvariant());
        if (hostKind != DeviceKind.Unknown) return hostKind;

        // 4) Purpose-built makers beat the port heuristics – a printer/phone/UPS/camera/NAS stays what
        //    it is even though it also serves a web UI, SNMP and scan-to-mail.
        var vendorKind = VendorKind(v);
        if (vendorKind is DeviceKind.Printer or DeviceKind.Phone or DeviceKind.Ups
            or DeviceKind.Camera or DeviceKind.Nas or DeviceKind.PaymentTerminal
            or DeviceKind.Franking or DeviceKind.Management or DeviceKind.Audio
            or DeviceKind.GameConsole or DeviceKind.Tv)
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
