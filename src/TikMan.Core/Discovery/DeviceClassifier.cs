namespace TikMan.Core.Discovery;

/// <summary>Rough kind of a discovered device, guessed from its vendor (OUI) and open ports.
/// Purely heuristic — meant as a hint in the list, never authoritative.</summary>
public enum DeviceKind
{
    Unknown,
    Router,
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
        ("tasmota", DeviceKind.IoT),
        ("amazon tech", DeviceKind.IoT), ("google", DeviceKind.IoT), ("signify", DeviceKind.IoT),
        ("philips lighting", DeviceKind.IoT), ("tp-link", DeviceKind.Switch),
        ("cisco", DeviceKind.Switch), ("juniper", DeviceKind.Switch), ("netgear", DeviceKind.Switch),
        ("zyxel", DeviceKind.Switch), ("d-link", DeviceKind.Switch), ("aruba", DeviceKind.AccessPoint),
        ("ubiquiti", DeviceKind.AccessPoint), ("apple", DeviceKind.Pc), ("dell", DeviceKind.Pc),
        ("lenovo", DeviceKind.Pc), ("micro-star", DeviceKind.Pc), ("asustek", DeviceKind.Pc),
        ("gigabyte", DeviceKind.Pc), ("intel", DeviceKind.Pc), ("samsung", DeviceKind.Pc),
    };

    /// <summary>Guesses the device kind. Open ports take priority over the vendor, since a running
    /// service is stronger evidence than who made the network card.</summary>
    public static DeviceKind Guess(string? vendor, IReadOnlyCollection<int> openPorts)
    {
        var ports = openPorts ?? Array.Empty<int>();
        bool Has(int p) => ports.Contains(p);

        // Service-based signals (strongest).
        if (Has(9100) || Has(515) || Has(631)) return DeviceKind.Printer;
        if (Has(554)) return DeviceKind.Camera;                 // RTSP
        if (Has(5060)) return DeviceKind.Phone;                 // SIP
        if (Has(8291)) return DeviceKind.Router;                // MikroTik Winbox

        // Trailing space so "…APC" at the end of a vendor name still matches the "apc " fragment.
        var v = (vendor ?? "").ToLowerInvariant() + " ";
        foreach (var (fragment, kind) in VendorHints)
            if (v.Contains(fragment)) return kind;

        // Weaker port fallbacks once the vendor gave nothing away.
        if (Has(445) || Has(139)) return DeviceKind.Pc;         // Windows/SMB host
        if (Has(80) || Has(443)) return DeviceKind.Server;      // some web-facing box

        return DeviceKind.Unknown;
    }
}
