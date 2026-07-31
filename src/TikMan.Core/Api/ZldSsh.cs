using System.Text.RegularExpressions;
using Renci.SshNet;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Queries Zyxel <b>ZLD</b> firewalls (USG / ZyWALL / USG FLEX / ATP) over their SSH CLI.
///
/// <para>⚠️ A different OS from the Zyxel <i>switches</i>: those run ZyNOS and answer
/// <c>show system-information</c> (see <see cref="ZyxelSsh"/>); ZLD does not have that command
/// (<c>% Parse error</c>) and lays its facts out differently. Its <c>show version</c> prints a two-image
/// table (running + standby); the running image carries the model and firmware. CPU, memory, uptime and the
/// serial each have their own <c>show …</c> command. Verified against a real USG FLEX 500 on ZLD V5.39.</para>
///
/// <para>⚠️ Same SSH quirk as every Zyxel box: the firewall miscomputes the encrypt-then-MAC HMACs, so the
/// connection drops the ETM variants (<see cref="SshCompat.WithCompatibleMacs"/>). And, like the switches,
/// it needs an interactive shell with a PTY – its sshd does not serve an exec channel usefully.</para></summary>
public static class ZldSsh
{
    /// <summary>What we show in the list for a ZLD firewall.</summary>
    public readonly record struct ZldInfo(string Model, string Firmware, string Serial);

    /// <summary>Model + running firmware + serial, or null when the device could not be read (offline, wrong
    /// login, or not actually a ZLD box). Used to fill the row after a login is set.</summary>
    public static async Task<ZldInfo?> GetInfoAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show version", "show serial-number" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 1) return null;
        var (model, firmware, _) = ParseVersion(outputs[0]);
        var serial = outputs.Count > 1 ? ParseSerial(outputs[1]) : "";
        // ⚠️ A ZLD read that yields neither model nor firmware is not a ZLD device (or the command errored) –
        // report failure rather than a blank record, so a non-firewall that happens to answer SSH is not
        // relabelled a firewall with empty facts.
        if (model.Length == 0 && firmware.Length == 0) return null;
        return new ZldInfo(model, firmware, serial);
    }

    /// <summary>CPU %, memory %, uptime and running firmware for the monitoring columns / history chart –
    /// the firewall analogue of RouterOS's <c>/system resource</c>. Null when SSH did not answer.
    /// <para>All commands run in ONE shell session; the firewall is happy with several, but one session keeps
    /// it cheap. Memory is reported as a percentage by ZLD, so it goes in <see cref="ResourceInfo.MemoryPercent"/>
    /// rather than the byte fields.</para></summary>
    public static async Task<ResourceInfo?> GetResourceAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show cpu status", "show mem status", "show system uptime", "show version" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 4) return null;
        var cpu = ParseCpu(outputs[0]);
        var mem = ParseMemUsage(outputs[1]);
        var uptime = ParseUptime(outputs[2]);
        var (_, firmware, _) = ParseVersion(outputs[3]);
        return new ResourceInfo
        {
            CpuLoad = cpu >= 0 ? cpu : 0,
            MemoryPercent = mem >= 0 ? mem : null,
            Uptime = uptime,
            Version = firmware,
        };
    }

    // ---- pure parsers (pinned against real USG FLEX 500 output in the smoke test) ---------------------

    /// <summary>The model, running firmware and boot status out of <c>show version</c>.
    ///
    /// <para>The table has one row per boot image; the one marked <c>Running</c> is the live firmware, the
    /// <c>Standby</c> row is the previous image kept for rollback. Columns are space-aligned and the model
    /// itself contains spaces ("USG FLEX 500"), so the row is read by shape, not by fixed offsets: an image
    /// number, then the model, then a <c>V…(CODE.n)</c> firmware token, then a date, then the status.</para>
    ///
    /// <para>Returns the <c>Running</c> row; falls back to the first parseable row if none says Running.</para></summary>
    public static (string Model, string Firmware, string BootStatus) ParseVersion(string output)
    {
        (string Model, string Firmware, string BootStatus) first = ("", "", "");
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            // <n> <model…> <V5.39(ABUJ.1)> <yyyy-mm-dd hh:mm:ss> <Running|Standby>
            var m = Regex.Match(line,
                @"^\d+\s+(.+?)\s+(V\d[\d.]*\([A-Za-z]{2,6}\.\d+\)[A-Za-z0-9]*)\s+\d{4}-\d{2}-\d{2}\b.*?\b(Running|Standby)\b",
                RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var row = (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim());
            if (row.Item3.Equals("Running", StringComparison.OrdinalIgnoreCase)) return row;
            if (first.Model.Length == 0) first = row;
        }
        return first;
    }

    /// <summary>The current CPU utilisation from <c>show cpu status</c> ("CPU utilization: 5 %" → 5). The
    /// FIRST line is the instantaneous value; the "for 1 min"/"for 5 min" lines are averages and skipped.
    /// -1 when nothing matched.</summary>
    public static int ParseCpu(string output)
    {
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            // Anchor on the exact label so a "for 1 min" line can't win – it also starts "CPU utilization".
            var m = Regex.Match(line, @"^CPU utilization:\s*(\d{1,3})\s*%", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct) && pct <= 100) return pct;
        }
        return -1;
    }

    /// <summary>Memory usage percent from <c>show mem status</c> ("memory usage: 29%" → 29). -1 when absent.</summary>
    public static int ParseMemUsage(string output)
    {
        var m = Regex.Match(output ?? "", @"memory usage:\s*(\d{1,3})\s*%", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var pct) && pct <= 100 ? pct : -1;
    }

    /// <summary>Uptime string from <c>show system uptime</c> ("system uptime: 00:39:48" → "00:39:48").</summary>
    public static string ParseUptime(string output)
    {
        var m = Regex.Match(output ?? "", @"system uptime:\s*(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>Serial from <c>show serial-number</c> ("serial number: S000L00000000" → the serial).</summary>
    public static string ParseSerial(string output)
    {
        var m = Regex.Match(output ?? "", @"serial number:\s*(\S+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>MAC → "PortN (interface)" from the ARP table, for the physical map – the same shape the switch
    /// FDB readers return, so devices sitting behind a firewall get placed too. Null when it did not answer.
    ///
    /// <para>The ARP table alone gives MAC → interface, and an interface like <c>lan1</c> is a port GROUP
    /// (a bridge over several physical ports), not a port. ZLD exposes no per-MAC physical port. But when a
    /// group has exactly ONE port with a link – read from <c>show port status</c> against the group's ports
    /// from <c>show port-grouping</c> – every device on that interface is reachable through it, so the label
    /// becomes "Port4 (lan1)": the physical port up front (like the switches show), the bridge in brackets.
    /// With zero or several active ports it stays the interface alone, because then it cannot be pinned.</para></summary>
    public static async Task<Dictionary<string, string>?> GetFdbAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show arp-table", "show port status", "show port-grouping" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 1) return null;
        var arp = ParseArpTable(outputs[0]);                                     // MAC -> interface
        if (arp.Count == 0) return null;
        var up = outputs.Count > 1 ? ParseActivePorts(outputs[1]) : new HashSet<int>();
        var groups = outputs.Count > 2 ? ParsePortGrouping(outputs[2]) : new();
        return arp.ToDictionary(kv => kv.Key, kv => PortLabel(kv.Value, up, groups),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Turns a bare interface into a port-aware label:
    /// <list type="bullet">
    /// <item>exactly one of the group's ports has a link → "Port4 (lan1)" – the device is definitely on it;</item>
    /// <item>several ports are live → "lan1 (Port 4/8)" – it is on ONE of those, but ZLD can't say which, so
    ///       the candidates help a debugger narrow it down;</item>
    /// <item>the interface is unknown or has no live port → the interface alone.</item>
    /// </list></summary>
    public static string PortLabel(string iface, HashSet<int> activePorts, Dictionary<string, List<int>> groups)
    {
        if (!groups.TryGetValue(iface, out var ports)) return iface;
        var active = ports.Where(activePorts.Contains).OrderBy(p => p).ToList();
        if (active.Count == 1) return $"Port{active[0]} ({iface})";
        if (active.Count > 1) return $"{iface} (Port {string.Join("/", active)})";
        return iface;
    }

    /// <summary>The physical ports with a link from <c>show port status</c> (any status but "Down").
    /// <code>4       1000M/Full  430175    273179 …</code> → 4 is up; a "Down" row is not.</summary>
    public static HashSet<int> ParseActivePorts(string output)
    {
        var up = new HashSet<int>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^(\d+)\s+(\S+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p)
                && !m.Groups[2].Value.Equals("Down", StringComparison.OrdinalIgnoreCase))
                up.Add(p);
        }
        return up;
    }

    /// <summary>interface → its physical port numbers, from <c>show port-grouping</c>.
    /// <code>4   lan1   no yes yes yes yes yes yes yes</code> → lan1 = {2,3,4,5,6,7,8} (Port1 no … Port8 yes).</summary>
    public static Dictionary<string, List<int>> ParsePortGrouping(string output)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], out _)) continue;    // skip header / blanks
            var name = parts[1];
            var ports = new List<int>();
            for (var i = 2; i < parts.Length; i++)
                if (parts[i].Equals("yes", StringComparison.OrdinalIgnoreCase)) ports.Add(i - 1); // Port(i-1)
            if (ports.Count > 0) map[name] = ports;
        }
        return map;
    }

    /// <summary>MAC → interface out of <c>show arp-table</c>.
    ///
    /// <para>⚠️ A firewall is not a switch: the "port" here is one of its INTERFACES (lan1 / lan2 / dmz …),
    /// not a switch port, so every device on one LAN segment shares an interface. It still segments them by
    /// interface and anchors them under the gateway on the map. The line shape, on a USG FLEX 500 (address
    /// and MAC anonymised):
    /// <code>10.0.0.23   ether   3c:18:a0:aa:bb:cc   C   lan1</code>
    /// address, HW type, MAC, flags, [mask], interface (the trailing token). Rows with no MAC (an incomplete
    /// entry, or the header) are skipped.</para></summary>
    public static Dictionary<string, string> ParseArpTable(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var mac = "";
            foreach (var p in parts) { var m = NormaliseMac(p); if (m.Length > 0) mac = m; }
            if (mac.Length == 0) continue;                              // header / incomplete ARP entry
            var iface = parts[^1];
            // The interface is the trailing token – reject a row that ends at the MAC or a non-name token.
            if (NormaliseMac(iface).Length > 0 || !Regex.IsMatch(iface, @"^[A-Za-z][\w.\-]*$")) continue;
            map[mac] = iface;
        }
        return map;
    }

    /// <summary>A MAC in canonical colon form, or "" when the token is not one.</summary>
    private static string NormaliseMac(string token)
    {
        var hex = new string((token ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return "";
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToUpperInvariant();
    }

    // ---- transport: interactive shell, one session per call ------------------------------------------

    private static async Task<List<string>?> RunAsync(string host, int port, string user, string password,
        IReadOnlyList<string> commands, CancellationToken ct, TimeSpan? budget = null)
    {
        var per = budget ?? TimeSpan.FromSeconds(30);
        try
        {
            return await Task.Run(() =>
            {
                var info = new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
                    new PasswordAuthenticationMethod(user, password)) { Timeout = TimeSpan.FromSeconds(20) };
                info.WithCompatibleMacs();

                using var ssh = new SshClient(info);
                ssh.Connect();
                using var shell = ssh.CreateShellStream("vt100", 200, 4000, 0, 0, 1 << 20);

                ReadUntilPrompt(shell, TimeSpan.FromSeconds(8));   // login banner + first prompt

                var outputs = new List<string>(commands.Count);
                foreach (var cmd in commands)
                {
                    ct.ThrowIfCancellationRequested();
                    shell.WriteLine(cmd);
                    outputs.Add(ReadUntilPrompt(shell, per));
                }

                try { shell.WriteLine("exit"); } catch { }
                ssh.Disconnect();
                return outputs;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }   // unreachable, SSH off, bad login – the caller decides
    }

    /// <summary>The running configuration as text – the config backup for a ZLD firewall (its <c>show
    /// running-config</c>, the same idea as the switches' one). ZLD has no binary backup artefact – the
    /// config file <i>is</i> the whole backup – so this is the only backup these boxes offer.
    /// <para>⚠️ Carries secrets ("… password … cipher …", pre-shared keys): it belongs in the user's backup
    /// file and NOWHERE else – never logged, never echoed. The command echo and trailing prompt are trimmed.</para></summary>
    public static async Task<string?> GetRunningConfigAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show running-config" }, ct, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
        if (outputs is null || outputs.Count == 0) return null;
        var text = CleanOutput(outputs[0], "show running-config");
        // A parse error / refusal is not a config; report failure rather than save the error text.
        return text.Length > 0 && !text.Contains("Parse error", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    /// <summary>The device log, newest first, in the shared <see cref="LogEntry"/> shape. Bounded read: the
    /// entries print newest-first, so a short budget still yields the freshest ones (a full ZLD log is
    /// thousands of lines and paging all of it over SSH is slow).</summary>
    public static async Task<List<LogEntry>?> GetLogAsync(string host, int port, string user, string password,
        int maxEntries = 500, CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show logging entries" }, ct, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        if (outputs is null || outputs.Count == 0) return null;
        return ParseLog(outputs[0], maxEntries);
    }

    /// <summary>Parses <c>show logging entries</c> into the shared <see cref="LogEntry"/> shape.
    ///
    /// <para>Each event is a MULTI-line record, verified against a real USG FLEX 500:
    /// <code>
    /// 1    2026-08-01 10:33:58 &lt;src-ip&gt;
    ///                          &lt;dst-ip&gt;
    ///      notice              user                   Account: admin
    ///      &lt;blank interface/protocol/country lines&gt;
    ///      Administrator admin from ssh has logged out Device
    /// </code>
    /// A record starts with "&lt;n&gt; &lt;date time&gt; …"; the priority/category line gives the Topics
    /// ("user/notice"), and the Message is the last non-empty line of the record (the human-readable text at
    /// the bottom), falling back to the note when there is none.</para></summary>
    public static List<LogEntry> ParseLog(string output, int maxEntries = 500)
    {
        var list = new List<LogEntry>();
        var body = new List<string>();
        string time = "", topics = "", note = "";

        void Flush()
        {
            if (time.Length == 0) return;
            var message = "";
            foreach (var l in body)
            {
                var s = l.Trim();
                if (s.Length > 0) message = s;   // ends on the last non-empty line = the Message field
            }
            if (message.Length == 0) message = note;
            list.Add(new LogEntry { Time = time, Topics = topics, Message = message });
        }

        foreach (var raw in (output ?? "").Split('\n'))
        {
            var start = Regex.Match(raw, @"^\s*\d+\s+(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\b");
            if (start.Success)
            {
                Flush();
                if (list.Count >= maxEntries) return list;
                body.Clear();
                time = start.Groups[1].Value;
                topics = ""; note = "";
                continue;
            }
            if (time.Length == 0) continue;   // header preamble before the first record

            var pri = Regex.Match(raw,
                @"^\s+(emerg\w*|alert|crit\w*|err\w*|warn\w*|notice|info\w*|debug)\s+(\S+)\s+(.*)$",
                RegexOptions.IgnoreCase);
            if (pri.Success && topics.Length == 0)
            {
                topics = $"{pri.Groups[2].Value}/{pri.Groups[1].Value}";
                note = pri.Groups[3].Value.Trim();
                continue;
            }
            body.Add(raw);
        }
        Flush();
        return list;
    }

    /// <summary>Strips the echoed command and the trailing prompt from a command's captured output.</summary>
    private static string CleanOutput(string raw, string command)
    {
        var lines = new List<string>();
        foreach (var line in (raw ?? "").Split('\n'))
        {
            var s = line.TrimEnd('\r', ' ');
            if (s.Trim() == command) continue;                        // the echo of what we typed
            if (Regex.IsMatch(s, @"^[\w.\-]{1,40}[>#]\s*$")) continue; // a bare prompt line
            lines.Add(s);
        }
        return string.Join("\n", lines).Trim('\n', ' ');
    }

    /// <summary>Reads a command's output, answering the pager and stopping when the ZLD prompt ("Router&gt;")
    /// returns. Prompt-driven with an idle fallback for output that ends without a fresh prompt.</summary>
    private static string ReadUntilPrompt(ShellStream shell, TimeSpan budget)
    {
        var sb = new System.Text.StringBuilder();
        var deadline = DateTime.UtcNow + budget;
        var lastData = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            var chunk = shell.Read();
            if (chunk.Length == 0)
            {
                if ((DateTime.UtcNow - lastData).TotalMilliseconds > 900) break;
                Thread.Sleep(25);
                continue;
            }
            sb.Append(chunk);
            lastData = DateTime.UtcNow;
            var tail = sb.Length <= 160 ? sb.ToString() : sb.ToString(sb.Length - 160, 160);
            if (tail.Contains("--More--", StringComparison.OrdinalIgnoreCase)) { shell.Write(" "); continue; }
            // The ZLD exec prompt is "<name># " or "<name}> " – stop as soon as it comes back.
            if (Regex.IsMatch(tail, @"[\w.\-]{1,40}[>#]\s*$")) break;
        }
        return sb.ToString();
    }
}
