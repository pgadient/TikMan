using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Reads facts from a Ubiquiti <b>UniFi</b> device over its SSH CLI (BusyBox/OpenSSH). Uses the
/// <see cref="SshSessionPool"/> exec channel — like RouterOS, and unlike the Zyxel PTY connectors — because a
/// UniFi device runs a clean OpenSSH server with a working exec channel and needs no pager handling.
///
/// <para>Credentials: a factory/standalone device answers to its vendor factory default (which differs between
/// older and newer firmware); an adopted device uses the site-specific SSH login the controller set
/// (Settings → System → Device Authentication). The user supplies whichever applies, exactly like every other
/// vendor here.</para>
///
/// <para>⚠️ The <c>info</c> output is UNTRUSTED text. <see cref="ParseInfo"/> is a pure key/value reader that
/// treats every line as data — no field is ever interpreted as a command. Bounded and null-safe throughout.</para>
///
/// <para>⚠️ The <c>info</c> field names are from the documented CLI; pin <see cref="ParseInfo"/> against a
/// real device's output (the smoke fixture is synthetic until then).</para></summary>
public static class UnifiSsh
{
    /// <summary>Model, firmware, MAC, hostname, uptime, whether the device is adopted, the hardware serial, and
    /// the board short-name — the row-filling facts. <paramref name="Adopted"/> is null when the status line
    /// didn't say; <paramref name="Serial"/> is "" when the serial file couldn't be read.
    /// <para><paramref name="Platform"/> is the board short-name from <c>/etc/board.info</c> (e.g. "USL16LPB").
    /// ⚠️ It is EXACTLY the <c>platform</c> key Ubiquiti's firmware API uses, so it drives the "latest firmware"
    /// lookup with no fragile model→code table. "" when the board file couldn't be read.</para>
    /// <para><paramref name="InformUrl"/> is the controller inform URL parsed from the status line (e.g.
    /// "http://10.0.0.8:8080/inform"); "" when the status carried none. On a factory/standalone device this is
    /// the default "http://unifi:8080/inform" placeholder – the caller only surfaces it when actually adopted.</para></summary>
    public readonly record struct UnifiInfo(string Model, string Version, string Mac, string Hostname,
        TimeSpan? Uptime, bool? Adopted, string Serial, string Platform, string InformUrl);

    private static ConnectionInfo Info(string host, int port, string user, string password) =>
        // UniFi runs a standard OpenSSH server – no Zyxel encrypt-then-MAC workaround needed.
        new(host, port is > 0 and <= 65535 ? port : 22, user,
            new PasswordAuthenticationMethod(user, password))
        { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Model + firmware + MAC + hostname + uptime + adopted-state, or null when the device could not
    /// be read (offline, wrong login, or not a UniFi box). Tries <c>mca-cli-op info</c> first (current
    /// firmware – verified on a USW-Lite-16-PoE, UniFi 7.0.50, where a bare <c>info</c> is "not found"), then
    /// the legacy <c>info</c>, over ONE held, serialized SSH session.</summary>
    public static async Task<UnifiInfo?> GetInfoAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "mca-cli-op info", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || ParseInfo(text) is { Model.Length: 0, Version.Length: 0 })
            text = await RunAsync(host, port, user, password, "info", ct).ConfigureAwait(false) ?? text;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var info = ParseInfo(text);
        // A read that yields neither model nor firmware isn't a UniFi device (or the command errored) – report
        // failure rather than a blank record, so a non-UniFi box that happens to answer SSH isn't relabelled.
        if (info.Model.Length == 0 && info.Version.Length == 0) return null;

        // Serial + platform code: `mca-cli-op info` carries neither. /proc/ubnthal/system.info has the serial
        // (serialno=…, the canonical hardware serial), and /etc/board.info has the board short-name
        // (board.shortname=…, which is EXACTLY the firmware API's platform key). Both read in one exec over the
        // held session; a device missing either file just keeps that field empty.
        var sys = await RunAsync(host, port, user, password,
            "cat /proc/ubnthal/system.info; echo '___BOARD___'; cat /etc/board.info 2>/dev/null", ct).ConfigureAwait(false);
        var serial = ParseSerial(sys);
        var platform = ParsePlatform(sys);
        var result = info;
        if (serial.Length > 0) result = result with { Serial = serial };
        if (platform.Length > 0) result = result with { Platform = platform };
        return result;
    }

    /// <summary>Reads <c>serialno=</c> out of /proc/ubnthal/system.info. Pure and bounded so it can be pinned.
    /// The value is hex-ish (e.g. a MAC-derived serial); anything longer than a serial or non-serial-shaped is
    /// rejected rather than surfaced. "" when absent.</summary>
    public static string ParseSerial(string? sysinfo)
    {
        foreach (var raw in (sysinfo ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("serialno=", StringComparison.OrdinalIgnoreCase)) continue;
            var v = line[("serialno=".Length)..].Trim();
            if (v.Length is > 0 and <= 32 && v.All(ch => Uri.IsHexDigit(ch) || ch == '-'))
                return v.ToUpperInvariant();
        }
        return "";
    }

    /// <summary>Reads the board short-name (<c>board.shortname=</c> in /etc/board.info, e.g. "USL16LPB") — the
    /// firmware API's platform key. Pure and bounded; rejects a "(null)" placeholder and anything not
    /// code-shaped. "" when absent.</summary>
    public static string ParsePlatform(string? boardInfo)
    {
        foreach (var raw in (boardInfo ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("board.shortname=", StringComparison.OrdinalIgnoreCase)) continue;
            var v = line[("board.shortname=".Length)..].Trim();
            // Code-shaped only: alphanumerics (e.g. USL16LPB). "(null)" and empties are rejected.
            if (v.Length is > 0 and <= 24 && v.All(char.IsLetterOrDigit))
                return v.ToUpperInvariant();
        }
        return "";
    }

    private static async Task<string?> RunAsync(string host, int port, string user, string password,
        string command, CancellationToken ct)
    {
        try
        {
            var session = SshSessionPool.GetOrCreate(SshSessionPool.KeyFor(host, port),
                () => new SshSession(() => Info(host, port, user, password)));   // exec-only (no shell)
            return await session.RunClientAsync(client =>
            {
                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(20);
                return cmd.Execute();
            }, ct).ConfigureAwait(false);
        }
        catch { return null; }   // SSH off / bad creds / not a UniFi device
    }

    /// <summary>Drops the held SSH session – call on a credential change so the next read rebuilds it.</summary>
    public static void InvalidateSession(string host, int port) =>
        SshSessionPool.Invalidate(SshSessionPool.KeyFor(host, port));

    /// <summary>Parses UniFi <c>info</c> output — a block of "Key: value" lines. Pure and defensive, so the
    /// smoke test can pin it. Unknown keys are ignored; keys are matched case-insensitively; the first value
    /// for a key wins.
    /// <code>
    /// Model:       USW-Lite-16-PoE           (shape verbatim from a real switch; values anonymised)
    /// Version:     7.0.50.00000
    /// MAC Address: 00:11:22:aa:bb:cc
    /// IP Address:  10.0.0.5
    /// Hostname:    office-sw
    /// Uptime:      123456 seconds
    /// NTP:         Synchronized
    /// Status:      Unable to resolve (http://unifi:8080/inform)    ← factory/standalone (not adopted)
    /// </code></summary>
    public static UnifiInfo ParseInfo(string output)
    {
        string model = "", version = "", mac = "", host = "", status = "", statusRaw = "";
        TimeSpan? uptime = null;

        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var val = line[(colon + 1)..].Trim();
            if (val.Length == 0) continue;

            switch (key)
            {
                case "model" when model.Length == 0: model = val; break;
                case "version" when version.Length == 0: version = val; break;
                case "firmware" when version.Length == 0: version = val; break;
                case "mac address" when mac.Length == 0: mac = NormalizeMac(val); break;
                case "hostname" when host.Length == 0: host = val; break;
                case "uptime" when uptime is null:
                    var m = Regex.Match(val, @"\d+");                 // "123456 seconds" / "123456"
                    if (m.Success && long.TryParse(m.Value, out var secs) && secs is >= 0 and < 4_000_000_000)
                        uptime = TimeSpan.FromSeconds(secs);
                    break;
                // Keep the raw status for the URL (case-preserving) and a lower-cased copy for keyword matching.
                case "status" when status.Length == 0: statusRaw = val; status = val.ToLowerInvariant(); break;
            }
        }

        bool? adopted = status.Length == 0 ? null
            : status.Contains("connected") || status.Contains("adopted") ? true
            // "unable to resolve …/inform" is what a factory/standalone device shows – it is looking for a
            // controller and can't find one, i.e. not managed.
            : status.Contains("pending") || status.Contains("unadopted") || status.Contains("isolated")
              || status.Contains("unable to resolve") || status.Contains("cannot resolve") ? false
            : (bool?)null;

        // The inform URL, if the status carried one ("… (http://host:8080/inform)"). Best-effort; on a factory
        // device this is the default "unifi" placeholder, which the caller ignores until the device is adopted.
        var urlMatch = Regex.Match(statusRaw, @"https?://[^\s)]+");
        var informUrl = urlMatch.Success ? urlMatch.Value : "";

        return new UnifiInfo(model, version, mac, host, uptime, adopted, "", "", informUrl);
    }

    // A MAC token to canonical upper "AA:BB:CC:DD:EE:FF"; "" if it isn't a MAC (leaves junk out of the field).
    private static string NormalizeMac(string s)
    {
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return "";
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>The device's running configuration (<c>/tmp/system.cfg</c>) as text, for a config backup.
    /// <para>⚠️ This file carries account secrets — <c>users.N.password</c> hashes and any RADIUS/SNMP keys.
    /// It goes STRAIGHT to the backup file and is NEVER logged (same rule as the ZLD/TP-Link running-config).
    /// </para> Null when the file could not be read, so a shell error is never written out as a "backup".</summary>
    public static async Task<string?> GetRunningConfigAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "cat /tmp/system.cfg", ct).ConfigureAwait(false);
        // A real UniFi config is a block of key=value lines; a "No such file" / permission error is not. The
        // '=' guard keeps a failed read from being saved as an empty or error "backup".
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('=')) return null;
        return text;
    }

    /// <summary>The config backup as a ZIP: the running config <c>/tmp/system.cfg</c> plus every persistent
    /// config file under <c>/etc/persistent/cfg/</c> (empty on a factory device; on an ADOPTED device it holds
    /// <c>mgmt</c> with the controller binding – inform URL + adoption authkey – which is exactly what makes
    /// this backup useful for getting a device back under its controller).
    /// <para>This is deliberately a text-config bundle, NOT a binary artefact. Each file is base64-framed over
    /// one exec (binary-safe), then packed into a ZIP in memory.</para>
    /// <para>⚠️ The bundle carries secrets (<c>users.N.password</c> hashes, and <c>mgmt.authkey</c> once
    /// adopted) — it goes straight into the ZIP bytes and is NEVER logged. Null when the primary running config
    /// couldn't be read, so a failed read is never written out as an empty "backup".</para></summary>
    public static async Task<byte[]?> GetConfigBundleAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        // For each existing file: a "###F:<path>" header line, then its base64. base64 is present on UniFi
        // (busybox /usr/sbin/base64, verified) and keeps any non-text file intact.
        const string script =
            "for f in /tmp/system.cfg /etc/persistent/cfg/*; do " +
            "[ -f \"$f\" ] && { echo \"###F:$f\"; base64 \"$f\"; }; done";
        var text = await RunAsync(host, port, user, password, script, ct).ConfigureAwait(false);
        if (text is null) return null;

        var files = ParseBundle(text);
        // Require the running config specifically – if that one file didn't come back, it isn't a valid backup.
        if (!files.Any(f => f.Name == "system.cfg")) return null;
        return BuildZip(files);
    }

    /// <summary>Parses the base64-framed multi-file dump from <see cref="GetConfigBundleAsync"/> into
    /// (entry-name, bytes). Pure and defensive so it can be pinned; a block whose base64 doesn't decode is
    /// skipped rather than corrupting the archive.</summary>
    public static List<(string Name, byte[] Data)> ParseBundle(string text)
    {
        var result = new List<(string, byte[])>();
        string? path = null;
        var b64 = new StringBuilder();

        void Flush()
        {
            if (path is null) return;
            try
            {
                var bytes = Convert.FromBase64String(Regex.Replace(b64.ToString(), @"\s+", ""));
                result.Add((EntryName(path), bytes));
            }
            catch { /* not valid base64 (a shell error slipped in) → skip this block */ }
        }

        foreach (var raw in (text ?? "").Split('\n'))
        {
            if (raw.StartsWith("###F:", StringComparison.Ordinal))
            {
                Flush();
                path = raw[5..].Trim();
                b64.Clear();
            }
            else if (path is not null) b64.Append(raw.Trim());
        }
        Flush();
        return result;
    }

    // Maps a device path to a tidy ZIP entry name: the running config at the root, persistent files under a
    // "persistent/" folder so their provenance is obvious.
    private static string EntryName(string path)
    {
        if (path == "/tmp/system.cfg") return "system.cfg";
        const string persist = "/etc/persistent/cfg/";
        if (path.StartsWith(persist, StringComparison.Ordinal)) return "persistent/" + path[persist.Length..];
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static byte[] BuildZip(List<(string Name, byte[] Data)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(data, 0, data.Length);
            }
        return ms.ToArray();
    }

    /// <summary>CPU / memory / uptime for the monitoring columns, read login-only over SSH in one quick exec.
    /// <para>⚠️ CPU is the 1-minute LOAD AVERAGE scaled by core count, NOT <c>/proc/stat</c> utilisation: on the
    /// RTL838x switch SoC both <c>top</c> and <c>/proc/stat</c> report the CPU as permanently ~100 % busy (a
    /// kernel busy-poll thread pins it), while the load average — measured ~0.6 on an idle USW-Lite — is the
    /// figure that actually tracks work. Memory excludes reclaimable cache/buffers, the honest "in use" number.
    /// No <c>sleep</c>, nothing left running: the session frees for the next queued command immediately.</para>
    /// Null when the read failed.</summary>
    public static async Task<ResourceInfo?> GetResourceAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password,
            "cat /proc/loadavg; echo ___N___; grep -c '^cpu[0-9]' /proc/stat; echo ___M___; " +
            "grep -E '^(MemTotal|MemFree|Buffers|Cached):' /proc/meminfo; echo ___U___; cat /proc/uptime",
            ct).ConfigureAwait(false);
        return ParseResource(text);
    }

    /// <summary>Parses the combined /proc read from <see cref="GetResourceAsync"/>. Pure and defensive so the
    /// smoke test can pin it against real device output. Null when nothing usable came back.</summary>
    public static ResourceInfo? ParseResource(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var parts = output.Split(new[] { "___N___", "___M___", "___U___" }, StringSplitOptions.None);
        if (parts.Length < 4) return null;

        var res = new ResourceInfo();

        // CPU: 1-minute load average (first token of /proc/loadavg) over the core count. Clamped 0..100.
        var loadTok = parts[0].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int ncpu = int.TryParse(parts[1].Trim(), out var n) && n > 0 ? n : 1;
        if (loadTok.Length > 0 && double.TryParse(loadTok[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var load1))
            res.CpuLoad = Math.Clamp((int)Math.Round(load1 / ncpu * 100.0), 0, 100);

        // Memory: MemTotal is the total; "in use" excludes MemFree + Buffers + Cached (reclaimable), so the
        // percentage reflects what is actually consumed, not the kernel's page cache.
        long total = MemKb(parts[2], "MemTotal"), free = MemKb(parts[2], "MemFree"),
             buffers = MemKb(parts[2], "Buffers"), cached = MemKb(parts[2], "Cached");
        if (total > 0)
        {
            res.TotalMemory = total * 1024;
            res.FreeMemory = (free + buffers + cached) * 1024;   // reclaimable counted as free ⇒ honest used %
        }

        // Uptime: first token of /proc/uptime (seconds), formatted like the other vendors' column.
        var upTok = parts[3].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (upTok.Length > 0 && double.TryParse(upTok[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
            && secs is >= 0 and < 4_000_000_000)
            res.Uptime = FormatUptime(TimeSpan.FromSeconds(secs));

        // A read with neither CPU nor memory nor uptime is not a real reading.
        return res.CpuLoad == 0 && res.TotalMemory == 0 && res.Uptime.Length == 0 ? null : res;
    }

    private static long MemKb(string block, string key)
    {
        var m = Regex.Match(block ?? "", $@"^{Regex.Escape(key)}:\s*(\d+)\s*kB", RegexOptions.Multiline);
        return m.Success && long.TryParse(m.Groups[1].Value, out var kb) ? kb : 0;
    }

    private static string FormatUptime(TimeSpan ts) =>
        ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m"
        : ts.TotalHours >= 1 ? $"{ts.Hours}h {ts.Minutes}m"
        : $"{ts.Minutes}m";

    /// <summary>The switch forwarding table (MAC → physical port) for the physical topology map, read over SSH.
    /// <para>Source is the switch driver's own table <c>/proc/switch/mac_table</c> — lines
    /// <c>vlan=1,port=16,mac=aa:bb:cc:dd:ee:ff</c> (verified on a USW-Lite-16-PoE). This is the L2 evidence the
    /// map needs: which MAC the switch sees on which port. Same shape as the other vendors' FDB readers, so the
    /// topology builder consumes it unchanged. Null when the table couldn't be read (not a UniFi switch, no
    /// login).</para>
    /// (An AP/gateway simply returns an empty or absent table – harmless; only switches have switch ports.)</summary>
    public static async Task<Dictionary<string, string>?> GetFdbAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "cat /proc/switch/mac_table", ct).ConfigureAwait(false);
        if (text is null) return null;
        var fdb = ParseFdb(text);
        return fdb.Count > 0 ? fdb : null;
    }

    /// <summary>Parses <c>/proc/switch/mac_table</c> (<c>vlan=…,port=N,mac=…</c>) into MAC → "Port N". Pure and
    /// defensive so the smoke test can pin it; first port seen for a MAC wins (a MAC lives on one port).</summary>
    public static Dictionary<string, string> ParseFdb(string text)
    {
        var fdb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var m = Regex.Match(raw, @"\bport=(\d+),mac=([0-9a-fA-F:]{17})\b");
            if (!m.Success) continue;
            var mac = m.Groups[2].Value.ToUpperInvariant();
            if (!fdb.ContainsKey(mac)) fdb[mac] = "Port " + m.Groups[1].Value;
        }
        return fdb;
    }

    /// <summary>The AP's associated wireless clients as MAC → SSID, for the physical topology map – so a device
    /// joined to one of the AP's WLANs hangs off the AP under that SSID. An access point has no switch table, so
    /// <see cref="GetFdbAsync"/> returns nothing for it; this is the wireless equivalent, keyed the same way
    /// (MAC → a port label) so the topology builder consumes it unchanged.
    /// <para>Read over SSH in one shell pass: <c>iwconfig</c> enumerates the VAP interfaces with their ESSID and
    /// <c>wlanconfig &lt;vap&gt; list</c> lists the stations associated to each, emitting "&lt;client-mac&gt;
    /// &lt;ssid&gt;" per station. ⚠️ Verified on a UAP-HD (QCA driver): the VAPs are named <c>wifi0ap3</c> etc.
    /// (NOT <c>athN</c>), there is no <c>iw</c> command, and <c>brctl showmacs</c> is unsupported – so the
    /// per-VAP station list is the reliable source. Null when nothing could be read.</para></summary>
    public static async Task<Dictionary<string, string>?> GetWifiClientsAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        const string cmd =
            "for v in $(iwconfig 2>/dev/null | sed -n 's/^\\([a-z0-9]\\{1,\\}\\)[[:space:]].*ESSID.*/\\1/p'); do " +
            "e=$(iwconfig $v 2>/dev/null | sed -n 's/.*ESSID:\"\\([^\"]*\\)\".*/\\1/p'); " +
            "wlanconfig $v list 2>/dev/null | sed 1d | awk -v e=\"$e\" 'NF{print $1\" \"e}'; done";
        var text = await RunAsync(host, port, user, password, cmd, ct).ConfigureAwait(false);
        if (text is null) return null;
        var clients = ParseWifiClients(text);
        return clients.Count > 0 ? clients : null;
    }

    /// <summary>Parses the "&lt;client-mac&gt; &lt;ssid&gt;" lines from <see cref="GetWifiClientsAsync"/> into
    /// MAC → SSID. Pure and defensive so the smoke test can pin it. ⚠️ The SSID may contain spaces (e.g.
    /// "Guest WLAN [U]"), so ONLY the first whitespace-delimited token is the MAC and the trimmed remainder is
    /// the SSID. First SSID seen for a MAC wins (a station associates to one VAP at a time).</summary>
    public static Dictionary<string, string> ParseWifiClients(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var mac = line[..sp];
            if (!Regex.IsMatch(mac, @"^[0-9a-fA-F:]{17}$")) continue;
            var ssid = line[(sp + 1)..].Trim();
            if (ssid.Length == 0) continue;
            mac = mac.ToUpperInvariant();
            if (!map.ContainsKey(mac)) map[mac] = ssid;
        }
        return map;
    }

    /// <summary>The device log for the log tab. The system log is the plain syslog file <c>/var/log/messages</c>
    /// (plus the rotated <c>messages.0</c>) on switches and gateways. Reads the two in order and keeps the newest
    /// <paramref name="maxEntries"/> lines, so the SSH transfer stays bounded.
    /// <para>⚠️ UniFi APs frequently keep NO persistent <c>/var/log/messages</c> – their syslog lives only in the
    /// busybox in-memory ring buffer, read with <c>logread</c>. So when the files are absent/empty, fall back to
    /// <c>logread</c> rather than leaving the log tab blank (this is why AP logs "did nothing" before). Its lines
    /// carry a program tag instead of a hostname, which <see cref="ParseLog"/> tolerates – the message is kept,
    /// only the discarded hostname slot differs, so the pinned switch parsing is untouched.</para>
    /// <para>⚠️ Device log lines can carry secrets (an inform <c>authkey=…</c>, auth events). They are shown in
    /// the log viewer exactly like every other vendor's log – that IS the feature – but must never be written
    /// into TikMan's own operational log, and any src fixture uses anonymised lines.</para>
    /// Null when neither source could be read.</summary>
    public static async Task<List<LogEntry>?> GetLogAsync(string host, int port, string user, string password,
        int maxEntries = 500, CancellationToken ct = default)
    {
        var cap = maxEntries > 0 ? maxEntries : 1000;
        var text = await RunAsync(host, port, user, password,
            $"cat /var/log/messages.0 /var/log/messages /var/log/syslog 2>/dev/null | tail -n {cap}", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            text = await RunAsync(host, port, user, password,
                $"logread 2>/dev/null | tail -n {cap}", ct).ConfigureAwait(false);
        return text is null ? null : ParseLog(text, maxEntries);
    }

    /// <summary>Parses BSD-syslog lines — <c>"Aug 12 21:48:31 &lt;host&gt; daemon.info mcad: &lt;message&gt;"</c>
    /// — into the shared <see cref="LogEntry"/> shape. The <c>facility.level</c> token becomes Topics; the rest
    /// (program tag + text) is the Message. Pure and defensive so the smoke test can pin it against real output;
    /// a line without a syslog timestamp is treated as a continuation of the previous entry, never dropped.</summary>
    public static List<LogEntry> ParseLog(string text, int maxEntries = 0)
    {
        var list = new List<LogEntry>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;

            // "<Mon> <day> <HH:MM:SS> <hostname> <rest>"
            var m = Regex.Match(line, @"^(\w{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})\s+\S+\s+(.*)$");
            if (!m.Success)
            {
                if (list.Count > 0) list[^1].Message += " " + line.Trim();   // wrapped continuation line
                continue;
            }

            var time = m.Groups[1].Value;
            var rest = m.Groups[2].Value.Trim();
            var topics = "";
            // Peel a leading "facility.level" (daemon.info, authpriv.notice, kern.warn) into Topics.
            var fac = Regex.Match(rest, @"^([a-z0-9]+\.[a-z]+)\s+(.*)$", RegexOptions.IgnoreCase);
            if (fac.Success) { topics = fac.Groups[1].Value; rest = fac.Groups[2].Value.Trim(); }
            list.Add(new LogEntry { Time = time, Topics = topics, Message = rest });
        }

        if (maxEntries > 0 && list.Count > maxEntries)
            list.RemoveRange(0, list.Count - maxEntries);   // keep the newest N
        return list;
    }
}
