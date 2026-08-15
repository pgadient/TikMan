namespace TikMan.Core.Discovery;

/// <summary>One service badge for the device list: what to show, what colour, what a click should open.
/// <see cref="Url"/> empty means "not clickable" (a capability announced over mDNS rather than a port).</summary>
public sealed record ServiceBadge(string Name, string Url, string Colour, string Tooltip)
{
    public bool IsClickable => Url.Length > 0;
}

/// <summary>Turns a device's open ports into the colour-coded badges the list shows. UI-free so the WPF and
/// Avalonia clients (and, if it ever wants them, the web UI) derive exactly the same set.</summary>
public static class ServiceBadges
{
    /// <summary>The one colour every user-added port badge uses – a neutral slate that is clearly not one
    /// of the meaning-carrying service colours.</summary>
    public const string CustomColour = "#455A64";

    /// <summary>Tooltip prefix for a custom port badge. Localisation lives in the clients; Core keeps the
    /// English word so the badge is never blank if a key is missing.</summary>
    private const string Tip = "Port";

    /// <summary>Badge colour per service, grouped by kind: secure=green, web=orange, ssh=blue,
    /// insecure=red, file=teal, MikroTik=dark, remote/management=purple, else grey.</summary>
    public static string ColourFor(string service) => service switch
    {
        "https" or "imaps" or "ftps" or "smtps" or "api-ssl" or "submission" => "#2E9E44",
        "http" or "http-alt" => "#E67E22",
        "ssh" or "sftp" => "#3A5BA0",
        "telnet" or "ftp" => "#C0392B",
        "smb" or "netbios" or "rsync" => "#16A085",
        "winbox" => "#2B3A42",
        "rdp" or "vnc" => "#6C5CE7", // remote-desktop access
        "api" => "#8E44AD",
        "wmi" => "#7A3EA0",
        "dns" or "snmp" or "syslog" => "#7F8C8D",
        "smtp" or "imap" => "#2980B9",
        "jetdirect" or "lpd" or "ipp" => "#A0522D", // printing
        "sip" => "#C2185B",                          // telephony
        "rtsp" => "#00838F",                         // camera stream
        "ipmi" or "amt" => "#5D4037",                // out-of-band management (BMC)
        "airprint" or "airscan" => "#546E7A",        // mDNS-announced capability, not a port
        "discovery" => "#607D8B",                     // vendor discovery protocol (MNDP/ZON/UBNT), not a port
        _ => "#95A5A6",
    };

    /// <summary>The badges for a device: one per recognised open port, plus the mDNS-announced
    /// capabilities (AirPrint/AirScan) which have no port of their own.</summary>
    public static IReadOnlyList<ServiceBadge> For(string host, IReadOnlyList<int> openPorts,
        IReadOnlyDictionary<string, string> extraInfo)
    {
        // A bare IPv6 literal has to be bracketed before it can go into a URL.
        var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        var list = new List<ServiceBadge>();

        var custom = SubnetScanner.CustomPorts.ToHashSet();

        foreach (var port in openPorts.Distinct().OrderBy(p => p))
        {
            // ⚠️ A user-added port gets its number as the label and one shared colour – deliberately not a
            // per-port colour. The built-in colours encode a meaning (green = encrypted, red = plaintext,
            // purple = remote access); a custom port has no known meaning, so giving each its own hue would
            // be a rainbow that says nothing. One colour reads as "these are yours".
            if (custom.Contains(port))
            {
                // Still guess a URL: a custom port is usually a web UI, and a badge you can click is worth
                // more than one you can only look at.
                var guess = port == 443 ? $"https://{h}:{port}/" : $"http://{h}:{port}/";
                list.Add(new ServiceBadge(port.ToString(), guess, CustomColour, $"{Tip} {port}"));
                continue;
            }

            var svc = SubnetScanner.ServiceName(port);
            var url = svc switch
            {
                "ssh" => $"ssh://{h}",
                "telnet" => $"telnet://{h}",
                "ftp" => $"ftp://{h}/",
                "rdp" => $"rdp://{h}:{port}",
                "vnc" => $"vnc://{h}:{port}",
                "rtsp" => $"rtsp://{h}:{port}/",
                "https" or "api-ssl" => $"https://{h}:{port}/",
                "http" or "http-alt" => $"http://{h}:{port}/",
                _ => "",
            };
            list.Add(new ServiceBadge(svc, url, ColourFor(svc), url.Length > 0 ? url : svc));
        }

        if (extraInfo.ContainsKey("AirPrint"))
            list.Add(new ServiceBadge("airprint", "", ColourFor("airprint"), "AirPrint"));
        if (extraInfo.ContainsKey("AirScan"))
            list.Add(new ServiceBadge("airscan", "", ColourFor("airscan"), "AirScan"));

        // SNMP has no TCP port to scan (it's UDP/161), so its badge comes from the probe result the enricher
        // stored: "v1", "v2c", or "v1 v2c". Split into one badge per version the device actually answered.
        if (extraInfo.TryGetValue("SNMP", out var snmpVersions) && snmpVersions.Length > 0)
            foreach (var v in snmpVersions.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(new ServiceBadge("snmp " + v, "", ColourFor("snmp"), "SNMP " + v));

        // Discovery/announce protocols the device answered (MNDP / ZON / UBNT vendor discovery, plus the
        // generic mDNS / SSDP). Like SNMP they live off the TCP scan (UDP/L2), so the badge comes from the
        // enricher's flag, not an open port. Non-clickable, one shared colour so they read as a group distinct
        // from the service chips.
        foreach (var proto in new[] { "MNDP", "ZON", "UBNT", "mDNS", "SSDP" })
            if (extraInfo.ContainsKey(proto))
            {
                var tip = proto switch
                {
                    "MNDP" => "MikroTik discovery (MNDP)",
                    "ZON" => "Zyxel discovery (ZON)",
                    "UBNT" => "Ubiquiti Device Discovery",
                    "mDNS" => "mDNS / Bonjour",
                    _ => "UPnP / SSDP",
                };
                list.Add(new ServiceBadge(proto.ToLowerInvariant(), "", ColourFor("discovery"), tip));
            }

        return list;
    }
}
