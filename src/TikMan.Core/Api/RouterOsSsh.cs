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

    private static async Task<string?> RunAsync(string host, int port, string user, string password,
        string command, CancellationToken ct)
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
                    cmd.CommandTimeout = TimeSpan.FromSeconds(30);
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

    // ---- parsers (pure, tested) ----

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
