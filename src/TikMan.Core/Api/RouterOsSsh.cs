using System.Globalization;
using System.Text.RegularExpressions;
using Renci.SshNet;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Reads the same monitoring/topology data as the REST API, but over the encrypted SSH CLI –
/// the secure path when a device's HTTPS handshake is broken. Runs "/…/print" and parses the text
/// output. The parsers are static and pure so they can be tested against real device output. The
/// password is used only to authenticate; it is never logged.</summary>
public static class RouterOsSsh
{
    private static ConnectionInfo Info(string host, int port, string user, string password) =>
        new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
            new PasswordAuthenticationMethod(user, password)) { Timeout = TimeSpan.FromSeconds(12) }.WithCompatibleMacs();

    /// <summary><paramref name="timeout"/> defaults to 30s, which is plenty for a local print. Pass more
    /// for anything that leaves the device – an update check asks MikroTik's server and waits for it.</summary>
    private static async Task<string?> RunAsync(string host, int port, string user, string password,
        string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var ssh = new SshClient(Info(host, port, user, password));
                ssh.Connect();
                try
                {
                    using var cmd = ssh.CreateCommand(command);
                    cmd.CommandTimeout = timeout ?? TimeSpan.FromSeconds(30);
                    return cmd.Execute();
                }
                finally { if (ssh.IsConnected) ssh.Disconnect(); }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception) { return null; } // SSH off / bad creds / not RouterOS
    }

    // ---- public reads ----

    public static async Task<ResourceInfo?> GetResourceAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "/system resource print", ct);
        return text is null ? null : ParseResource(text);
    }

    public static async Task<List<(string Mac, string Port)>?> GetBridgeHostsAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "/interface bridge host print terse", ct);
        if (text is null) return null;
        var list = ParseBridgeFdb(text);
        return list.Count > 0 ? list : null;
    }

    public static async Task<List<(string Mac, string Port)>?> GetNeighborsAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "/ip neighbor print terse", ct);
        if (text is null) return null;
        var list = ParseNeighbors(text);
        return list.Count > 0 ? list : null;
    }

    public static async Task<Dictionary<string, string>?> GetWifiSsidsAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "/interface wifi print detail", ct);
        var map = text is null ? new() : ParseWifiSsids(text);
        if (map.Count == 0) // legacy driver
        {
            var legacy = await RunAsync(host, port, user, password, "/interface wireless print detail", ct);
            if (legacy is not null) map = ParseWifiSsids(legacy);
        }
        return map.Count > 0 ? map : null;
    }

    public static async Task<List<Models.LogEntry>?> GetLogAsync(string host, int port, string user, string password,
        int maxEntries, CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "/log print", ct);
        if (text is null) return null;
        var list = ParseLog(text, maxEntries);
        return list.Count > 0 ? list : null;
    }

    /// <summary>Triggers the RouterOS update install over SSH (<c>/system package update install</c>) –
    /// which downloads/installs and reboots. Returns true when the command was sent (the reboot cuts the
    /// connection, which is expected), false only when SSH itself couldn't be reached (so the caller can
    /// fall back to REST). Never touches HTTP.</summary>
    public static async Task<bool> InstallUpdateAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            SshClient ssh;
            try
            {
                ssh = new SshClient(Info(host, port, user, password));
                ssh.Connect();
            }
            catch (Exception) { return false; } // SSH unreachable / bad creds → let the caller try REST
            try
            {
                using var cmd = ssh.CreateCommand("/system package update install");
                cmd.CommandTimeout = TimeSpan.FromSeconds(20);
                try { cmd.Execute(); } catch { /* the reboot cuts the connection – expected */ }
            }
            catch { /* channel died as the device rebooted – still triggered */ }
            finally { try { if (ssh.IsConnected) ssh.Disconnect(); } catch { } ssh.Dispose(); }
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Checks for updates over the SSH CLI, optionally switching the channel first – the same
    /// pair REST does, for devices whose HTTPS handshake is broken. Null when SSH can't be reached, so
    /// the caller can fall back.
    /// <para>Pass <paramref name="channel"/> null to check without touching the channel.</para></summary>
    public static async Task<UpdateInfo?> CheckForUpdatesAsync(string host, int port, string user, string password,
        string? channel = null, CancellationToken ct = default)
    {
        var script = (channel is { Length: > 0 } c ? $"/system package update set channel={c}; " : "")
                   + "/system package update check-for-updates; /system package update print";
        var text = await RunAsync(host, port, user, password, script, ct, TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        return text is null ? null : ParseUpdate(text);
    }

    // ---- parsers (pure, tested) ----

    /// <summary>Parses <c>/system package update print</c> (real output from an L009 on 7.24beta3):
    /// <code>
    ///            channel: testing
    ///               mode: https
    ///  check-certificate: yes
    ///         ip-version: auto
    ///  installed-version: 7.24beta3
    ///     latest-version: 7.24rc1
    ///             status: New version is available
    /// </code>
    /// <para>⚠️ Last value wins, which is why this doesn't use <see cref="ParseColon"/>: that keeps the
    /// <i>first</i> value per key, and we run check-for-updates and print in one command. The check
    /// prints its own block first, with a status that reads "checking for updates..." while it is still
    /// talking to MikroTik – first-wins would hand back that half-finished block instead of the print's
    /// settled one. Keys we don't know (mode, check-certificate, ip-version) simply fall out.</para></summary>
    public static UpdateInfo ParseUpdate(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            var key = line[..c].Trim();
            if (key.Length > 0 && !key.Contains(' ')) d[key] = line[(c + 1)..].Trim(); // last wins
        }
        string Get(string key) => d.TryGetValue(key, out var v) ? v : "";
        return new UpdateInfo
        {
            Channel = Get("channel"),
            InstalledVersion = Get("installed-version"),
            LatestVersion = Get("latest-version"),
            Status = Get("status"),
        };
    }

    /// <summary>Parses a "key: value" block (/system resource, routerboard, identity) – the keys are
    /// right-aligned with leading spaces, one per line.</summary>
    public static Dictionary<string, string> ParseColon(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            var key = line[..c].Trim();
            var val = line[(c + 1)..].Trim();
            if (key.Length > 0 && !d.ContainsKey(key)) d[key] = val;
        }
        return d;
    }

    public static ResourceInfo ParseResource(string text)
    {
        var d = ParseColon(text);
        string G(string k) => d.TryGetValue(k, out var v) ? v : "";
        return new ResourceInfo
        {
            Version = G("version"),
            BoardName = G("board-name"),
            Uptime = G("uptime"),
            CpuLoad = ParsePercent(G("cpu-load")),
            FreeMemory = ParseBytes(G("free-memory")),
            TotalMemory = ParseBytes(G("total-memory")),
        };
    }

    private static int ParsePercent(string s)
    {
        var m = Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out var v) ? v : 0;
    }

    /// <summary>"3763.1MiB" / "128.0KiB" / "2.0GiB" → bytes (RouterOS uses binary units).</summary>
    private static long ParseBytes(string s)
    {
        var m = Regex.Match(s.Trim(), @"^([\d.]+)\s*(Ki|Mi|Gi|Ti)?B?", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return 0;
        double factor = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "ki" => 1024d,
            "mi" => 1024d * 1024,
            "gi" => 1024d * 1024 * 1024,
            "ti" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)(n * factor);
    }

    /// <summary>Parses "/interface bridge host print terse" → (MAC, port it was seen on / on-interface).
    /// Skips lines without a mac-address (headers/blank).</summary>
    public static List<(string Mac, string Port)> ParseBridgeFdb(string terse)
    {
        var list = new List<(string, string)>();
        foreach (var line in terse.Split('\n'))
        {
            var mac = Field(line, "mac-address");
            if (mac.Length == 0) continue;
            var port = Field(line, "on-interface");
            if (port.Length == 0) port = Field(line, "interface");
            var key = NormalizeMac(mac);
            if (key.Length == 12 && port.Length > 0) list.Add((key, port));
        }
        return list;
    }

    /// <summary>Parses "/ip neighbor print terse" → (MAC, physical port). The interface field can be a
    /// comma list like "sfp-sfpplus12,lan" (physical first, then the bridge) – we take the first token.</summary>
    public static List<(string Mac, string Port)> ParseNeighbors(string terse)
    {
        var list = new List<(string, string)>();
        foreach (var line in terse.Split('\n'))
        {
            var mac = Field(line, "mac-address");
            if (mac.Length == 0) continue;
            var iface = Field(line, "interface"); // "interface=" comes before "interface-name="; Field takes the first
            var port = iface.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var key = NormalizeMac(mac);
            if (key.Length == 12 && port.Length > 0) list.Add((key, port));
        }
        return list;
    }

    /// <summary>Parses "/interface wifi print detail" → interface name → SSID. Two shapes: a CAPsMAN
    /// CONTROLLER lists each interface with <c>name="…"</c> + <c>configuration.ssid="…"</c>; an AP/CAP
    /// only carries the SSID in the <c>;;; mode: AP, SSID: &lt;ssid&gt;, channel:</c> comment (its
    /// <c>name</c> is like "wifi1"). A trailing "[S]"/"[G]"/"[L]" is part of the network name – kept
    /// verbatim (that's what a client sees). Also covers the legacy "/interface wireless" layout.</summary>
    public static Dictionary<string, string> ParseWifiSsids(string detail)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in SplitRecords(detail))
        {
            var name = Cap(record, @"(?<![\w-])name=""([^""]*)""");
            if (name.Length == 0) continue;
            var ssid = Cap(record, @"(?<![\w-])(?:configuration\.)?ssid=""([^""]*)""");
            if (ssid.Length == 0)
            {
                // AP/CAP side: SSID only in the ";;; mode: AP, SSID: <ssid>, channel:" comment.
                var m = Regex.Match(record, @"SSID:\s*(.+?)(?:,\s*channel:|,\s*\w+:|\s+[\w.-]+=|$)");
                if (m.Success) ssid = m.Groups[1].Value.Trim();
            }
            if (ssid.Length > 0 && !map.ContainsKey(name)) map[name] = ssid;
        }
        return map;
    }

    /// <summary>Splits a "print detail" dump into one string per record. A record starts at a line that
    /// begins (after indent) with the entry number; its wrapped continuation lines are folded in.</summary>
    private static IEnumerable<string> SplitRecords(string text)
    {
        var sb = new System.Text.StringBuilder();
        bool started = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Regex.IsMatch(line, @"^\s*\d+\s+\S"))
            {
                if (started) { yield return sb.ToString(); sb.Clear(); }
                started = true;
                sb.Append(line);
            }
            else if (started) sb.Append(' ').Append(line.Trim());
        }
        if (started) yield return sb.ToString();
    }

    /// <summary>Parses "/log print" lines: "&lt;date&gt; &lt;time&gt; &lt;topics&gt; &lt;message&gt;".</summary>
    public static List<Models.LogEntry> ParseLog(string text, int maxEntries = 0)
    {
        var list = new List<Models.LogEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var m = Regex.Match(raw.TrimEnd('\r'), @"^\s*(\S+)\s+(\S+)\s+(\S+)\s+(.*)$");
            if (!m.Success) continue;
            list.Add(new Models.LogEntry
            {
                Time = m.Groups[1].Value + " " + m.Groups[2].Value,
                Topics = m.Groups[3].Value,
                Message = m.Groups[4].Value.Trim(),
            });
        }
        if (maxEntries > 0 && list.Count > maxEntries) list.RemoveRange(0, list.Count - maxEntries);
        return list;
    }

    private static string Cap(string s, string pattern)
    {
        var m = Regex.Match(s, pattern);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Value of "key=" up to the next whitespace (RouterOS terse fields are space-separated;
    /// the single-token fields we read never contain spaces).</summary>
    private static string Field(string line, string key)
    {
        var m = Regex.Match(line, $@"(?<![\w-]){Regex.Escape(key)}=(\S+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string NormalizeMac(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? hex : "";
    }
}
