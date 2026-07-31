using Renci.SshNet;
using Renci.SshNet.Common;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Facts read from a device: what we show in the list.</summary>
/// <param name="Serial">Serial number, when the device prints one.</param>
/// <param name="MacAddress">The switch's own MAC, as printed (dash-separated on TP-Link).</param>
/// <param name="Uptime">Running time, verbatim ("135 day - 7 hour - 50 min - 40 sec").</param>
/// <param name="Location">Contact/location strings the owner configured – often the only clue where a
/// switch physically is.</param>
public readonly record struct DeviceFacts(string Name, string Model, string HardwareVersion, string FirmwareVersion,
    string Serial = "", string MacAddress = "", string Uptime = "", string Location = "", string Contact = "",
    string Bootloader = "");

/// <summary>Queries TP-Link JetStream / Omada managed switches over SSH. These have no REST API,
/// but their CLI exposes <c>show system-info</c> in user-exec mode, which prints the firmware and
/// hardware version. The parser is locale-independent and label-driven so it survives small format
/// differences between models.</summary>
public static class TpLinkSshConnector
{
    /// <summary>Connects over SSH (Device.Port is the SSH port), runs <c>show system-info</c> and returns
    /// the parsed facts. Throws on connection/authentication failure so monitoring can show a status.
    ///
    /// <para>⚠️ Goes through <see cref="RunCommandsAsync"/> – the connector's own interactive-shell path –
    /// NOT the shared <see cref="SshExec"/> runner, and this was MEASURED. Two reasons SshExec fails here:
    /// its 6 s connect timeout is shorter than this switch's handshake (a real TP-Link takes ~8.3 s just to
    /// complete the SSH handshake), so the connect timed out before a command ever ran; and its exec-first
    /// path hangs on <c>show system-info</c> because <c>TPSSH</c> answers exec requests with nothing. The
    /// shell path here uses a 15 s timeout and the interactive CLI the device actually supports – exactly
    /// what the FDB, config and log reads already use, which is why those worked while the facts didn't.</para></summary>
    public static async Task<DeviceFacts> GetFactsAsync(Device device, string password, CancellationToken ct = default)
    {
        var port = device.Port is > 0 and <= 65535 ? device.Port : 22;
        var outputs = await RunCommandsAsync(device.Host, port, device.Username, password,
            new[] { "show system-info" }, ct).ConfigureAwait(false);
        return ParseSystemInfo(outputs is { Count: > 0 } ? outputs[0] : "");
    }

    /// <summary>Runs commands on the switch's interactive CLI and returns each one's output.
    ///
    /// <para>⚠️ A <b>shell</b>, not an exec channel. These appliances refuse <c>exec</c> requests, which is
    /// why the facts reader goes through <see cref="SshExec"/>'s shell path too – and why everything here
    /// has to cope with a prompt, a banner and a pager rather than a clean stream.</para>
    ///
    /// <para>⚠️ The pager is fed with spaces until it stops asking. Without that, every listing longer than
    /// a screen returns its first page and then blocks – which on this switch's 66-entry forwarding table
    /// would silently produce a third of the map.</para>
    ///
    /// <para>Returns null when the device cannot be reached or refuses the login; the caller then falls
    /// back to whatever it can do without credentials.</para></summary>
    /// <param name="enough">Optional per-command early stop – see the overload note on
    /// <c>ReadUntilPrompt</c>. Applied to every command in the batch.</param>
    public static async Task<List<string>?> RunCommandsAsync(string host, int port, string user, string password,
        IReadOnlyList<string> commands, CancellationToken ct = default, Func<string, bool>? enough = null)
    {
        try
        {
            return await Task.Run(() =>
            {
                // ⚠️ Generous timeouts on purpose. A real TP-Link takes ~8.3 s just to finish the SSH
                // handshake, and these switches accept only ONE session at a time – so during a scan, when a
                // probe and the monitor can reach for the same switch, an attempt sometimes waits on the
                // other finishing. 15 s connect / 8 s banner occasionally lost that race and the switch
                // dropped out of the scan; 25 s / 15 s gives it room without hanging the pass (the whole read
                // is still bounded).
                var info = new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
                    new PasswordAuthenticationMethod(user, password)) { Timeout = TimeSpan.FromSeconds(25) };
                info.WithCompatibleMacs();

                using var ssh = new SshClient(info);
                ssh.Connect();
                using var shell = ssh.CreateShellStream("vt100", 200, 200, 0, 0, 65536);

                ReadUntilPrompt(shell, TimeSpan.FromSeconds(15));     // login banner + first prompt
                // Privileged mode: several reads are refused in user-exec. An empty enable password is the
                // norm for an account that is already an admin; a failure here is harmless.
                shell.WriteLine("enable");
                ReadUntilPrompt(shell, TimeSpan.FromSeconds(5));

                var outputs = new List<string>(commands.Count);
                foreach (var cmd in commands)
                {
                    ct.ThrowIfCancellationRequested();
                    shell.WriteLine(cmd);
                    outputs.Add(ReadUntilPrompt(shell, TimeSpan.FromSeconds(45), enough));
                }

                try { shell.WriteLine("exit"); } catch { }
                ssh.Disconnect();
                return outputs;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }   // unreachable, SSH off, bad login – the caller decides
    }

    /// <summary>Reads a command's output, answering the pager immediately and stopping as soon as the shell
    /// prompt comes back.
    ///
    /// <para>⚠️ Prompt-driven, not "wait until it has been quiet for N seconds". The quiet-timeout version
    /// measured <b>97 seconds</b> for one log read: the pager interrupts roughly every 24 lines, and paying
    /// a fixed wait at each of ~40 interruptions is where the whole minute went. Answering the pager the
    /// instant it appears, and stopping the instant the prompt returns, does the same read in a fraction of
    /// that – nothing about the device changed, only how long we sit waiting for it.</para>
    ///
    /// <para>The overall budget is a safety net for a device that never returns a prompt; the short
    /// idle timeout catches output that simply ends without one.</para></summary>
    /// <param name="enough">Optional early stop: when it returns true for what has arrived so far, the
    /// listing is aborted with "Q" instead of being paged to the end. Measured on a real switch: the full
    /// log is 66 KB and takes <b>12 s</b> because the pager interrupts ~43 times, while the 200 entries the
    /// UI actually asked for are on the first few screens. Reading the other 800 only to throw them away is
    /// the entire cost.</param>
    private static string ReadUntilPrompt(Renci.SshNet.ShellStream shell, TimeSpan budget,
        Func<string, bool>? enough = null)
    {
        var sb = new System.Text.StringBuilder();
        var deadline = DateTime.UtcNow + budget;
        var lastData = DateTime.UtcNow;
        var quitting = false;

        while (DateTime.UtcNow < deadline)
        {
            var chunk = shell.Read();
            if (chunk.Length == 0)
            {
                // No prompt and nothing arriving: the output is over (or the device went quiet on us).
                if ((DateTime.UtcNow - lastData).TotalMilliseconds > 1200) break;
                Thread.Sleep(25);
                continue;
            }

            sb.Append(chunk);
            lastData = DateTime.UtcNow;

            // Only the tail matters: the pager marker stays in the buffer after being answered, so testing
            // the whole accumulated text would keep re-triggering for the rest of the read.
            var tail = Tail(sb, 160);
            if (PagerWaiting(tail))
            {
                // "Q" is what the pager itself offers ("Press any key to continue (Q to quit)"), so this is
                // the device's own way out, not a connection we drop on the floor – the prompt comes back
                // and the session stays usable.
                if (!quitting && enough is not null && enough(sb.ToString())) quitting = true;
                shell.Write(quitting ? "Q" : " ");
                continue;
            }
            if (PromptAtEnd(tail)) break;
        }
        return sb.ToString();
    }

    private static string Tail(System.Text.StringBuilder sb, int count) =>
        sb.Length <= count ? sb.ToString() : sb.ToString(sb.Length - count, count);

    private static bool PagerWaiting(string tail) =>
        tail.Contains("Press any key to continue", StringComparison.OrdinalIgnoreCase) ||
        tail.Contains("--More--", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the text ends at the CLI prompt ("sg2008-lab#" / "…>").
    /// <para>Matched by shape, not by the prompt's text: the prompt is the device's system name, which is
    /// different on every switch and changes the moment someone renames it.</para></summary>
    private static bool PromptAtEnd(string tail) =>
        System.Text.RegularExpressions.Regex.IsMatch(tail, @"[\w.\-]{1,40}[#>]\s*$");

    /// <summary>The forwarding table (MAC → port) for the physical map, over the SSH CLI. Null when the
    /// switch could not be read.</summary>
    public static async Task<Dictionary<string, string>?> GetFdbAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var outputs = await RunCommandsAsync(host, port, user, password,
            new[] { "show mac address-table" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count == 0) return null;
        var fdb = ParseMacAddressTable(outputs[0]);
        return fdb.Count > 0 ? fdb : null;
    }

    /// <summary>The switch's running configuration as text – the config backup for these devices.
    ///
    /// <para>⚠️ The output contains the account table, including password hashes
    /// (<c>user name admin privilege admin secret 5 &lt;hash&gt;</c>). That is exactly what a config backup
    /// is <i>for</i> and it belongs in the user's backup file – but it must never be logged, echoed to a
    /// status line, or written anywhere else. It is returned to the caller and nowhere else.</para>
    ///
    /// <para>The command echo and the trailing prompt are trimmed off, so the saved file starts at the
    /// config itself rather than at "show running-config".</para></summary>
    public static async Task<string?> GetRunningConfigAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var outputs = await RunCommandsAsync(host, port, user, password,
            new[] { "show running-config" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count == 0) return null;

        var text = CleanCommandOutput(outputs[0], "show running-config");
        // A refusal ("Error: Bad command") is not a config. Better to report failure than to save a file
        // whose contents are an error message.
        return text.Length > 0 && !text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    /// <summary>Strips the echoed command, the pager artefacts and the trailing prompt from one command's
    /// captured output, leaving just what the device printed.</summary>
    private static string CleanCommandOutput(string raw, string command)
    {
        var lines = new List<string>();
        foreach (var line in StripAnsi(raw).Split('\n'))
        {
            var s = line.TrimEnd('\r', ' ');
            if (s.Trim() == command) continue;                       // the echo of what we typed
            if (PagerWaiting(s)) continue;                           // "Press any key to continue …"
            if (PromptAtEnd(s) && s.Trim().Length <= 42) continue;    // a bare prompt line
            lines.Add(s);
        }
        return string.Join("\n", lines).Trim('\n', ' ');
    }

    /// <summary>The device log, newest first, in the shared <see cref="LogEntry"/> shape.</summary>
    public static async Task<List<LogEntry>?> GetLogAsync(string host, int port, string user, string password,
        int maxEntries = 500, CancellationToken ct = default)
    {
        // ⚠️ Stop as soon as the requested number of entries has arrived. The CLI has no "last N" form, so
        // without this the whole buffer is paged across the wire and then thrown away – measured at 12 s for
        // 66 KB versus a fraction of that for the first few screens. Counting '#' line starts is enough:
        // one log entry per line, and over-counting slightly only means reading one extra screen.
        var outputs = await RunCommandsAsync(host, port, user, password,
            new[] { "show logging buffer" }, ct,
            enough: text => CountLines(text, '#') >= maxEntries).ConfigureAwait(false);
        if (outputs is null || outputs.Count == 0) return null;
        return ParseLog(outputs[0], maxEntries);
    }

    private static int CountLines(string text, char startsWith)
    {
        var n = 0;
        foreach (var line in text.Split('\n'))
            if (line.TrimStart().StartsWith(startsWith)) n++;
        return n;
    }

    /// <summary>CPU and memory load in percent (-1 each when the switch did not report one).</summary>
    public static async Task<(int Cpu, int Memory)?> GetUtilisationAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var outputs = await RunCommandsAsync(host, port, user, password,
            new[] { "show cpu-utilization", "show memory-utilization" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 2) return null;
        return (ParseUtilisation(outputs[0]), ParseUtilisation(outputs[1]));
    }

    /// <summary>CPU %, memory % and uptime for the monitoring columns and history chart – the switch
    /// analogue of RouterOS's <c>/system resource</c>. Null when SSH didn't answer.
    /// <para>⚠️ All three commands run in ONE shell session (a single <see cref="RunCommandsAsync"/>): these
    /// switches allow only one SSH session at a time, so opening a fresh one per value would fight itself.
    /// Memory is a percentage only – TPSSH reports no byte totals – so it goes in ResourceInfo.MemoryPercent
    /// rather than the byte fields.</para></summary>
    public static async Task<ResourceInfo?> GetResourceAsync(string host, int port, string user,
        string password, CancellationToken ct = default)
    {
        var outputs = await RunCommandsAsync(host, port, user, password,
            new[] { "show cpu-utilization", "show memory-utilization", "show system-info" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 3) return null;
        var cpu = ParseUtilisation(outputs[0]);
        var mem = ParseUtilisation(outputs[1]);
        var facts = ParseSystemInfo(outputs[2]);
        return new ResourceInfo
        {
            CpuLoad = cpu >= 0 ? cpu : 0,
            MemoryPercent = mem >= 0 ? mem : null,
            Uptime = facts.Uptime,
            Version = facts.FirmwareVersion,
        };
    }

    /// <summary>Parses <c>show system-info</c>. Each line is "Label - value"; the label decides the field.
    ///
    /// <para>⚠️ The firmware label is <b>"Software Version"</b> on real JetStream firmware, not "Firmware
    /// Version" – measured against a TL-SG2008 v3.0. The old parser matched only "firmware", so the version
    /// column stayed empty on every one of these switches, and the invented test fixture ("Firmware
    /// Version - …") agreed with the code instead of with the device. Both labels are accepted now, and the
    /// fixture is the device's real output.</para>
    ///
    /// <para>Split on the first dash, which is why the value side may contain dashes of its own
    /// (a MAC, or "135 day - 7 hour - …") without being cut.</para></summary>
    public static DeviceFacts ParseSystemInfo(string output)
    {
        string name = "", model = "", hardware = "", firmware = "";
        string serial = "", mac = "", uptime = "", location = "", contact = "", bootloader = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int dash = line.IndexOf('-');
            if (dash <= 0) continue;
            var label = line[..dash].Trim().ToLowerInvariant();
            var value = line[(dash + 1)..].Trim();
            if (value.Length == 0) continue;

            // ⚠️ Bootloader before the firmware/software test: its label is "Bootloader Version", which
            // would otherwise be swallowed by a looser "version" match and shown as the firmware.
            if (label.Contains("bootloader")) bootloader = value;
            else if (label.Contains("firmware") || label.Contains("software")) firmware = value;
            else if (label.Contains("hardware")) hardware = value;
            else if (label is "device name" or "system name" or "hostname") name = value;
            else if (label.Contains("device model")) model = value;
            else if (label.Contains("serial")) serial = value;
            else if (label.Contains("mac address")) mac = value;
            else if (label.Contains("running time") || label.Contains("up time") || label == "uptime") uptime = value;
            else if (label.Contains("location")) location = value;
            else if (label.Contains("contact")) contact = value;
        }

        // The hardware line usually carries the model too, e.g. "TL-SG2008 3.0"; take the leading token.
        if (model.Length == 0 && hardware.Length > 0)
            model = hardware.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        return new DeviceFacts(name, model, hardware, firmware, serial, mac, uptime, location, contact, bootloader);
    }

    /// <summary>The hardware revision on its own ("TL-SG2008 3.0" → "3.0"), which is how TP-Link organises
    /// its download pages. Returns "" when the string carries no version part.</summary>
    public static string HardwareRevision(string hardwareVersion)
    {
        var m = System.Text.RegularExpressions.Regex.Match(hardwareVersion ?? "", @"(\d+(?:\.\d+)*)\s*$");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Parses <c>show mac address-table</c> into MAC → port, the same shape the SNMP and RouterOS
    /// readers produce – so the physical map can place devices behind a TP-Link switch too.
    ///
    /// <para>⚠️ Only <b>dynamic</b> entries, and the switch's own CPU/management entries are skipped: those
    /// say "this address is me", not "this address is reachable through that port", and treating them as
    /// evidence would hang the switch off its own port.</para></summary>
    public static Dictionary<string, string> ParseMacAddressTable(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = StripAnsi(raw).Trim();
            if (line.Length == 0) continue;

            // MAC VLAN PORT TYPE AGING – anything else (headers, rules, the pager, the total line) is skipped
            // by the shape test rather than by matching header text, which differs between firmwares.
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var mac = NormaliseMac(parts[0]);
            if (mac.Length == 0) continue;

            var port = parts[2];
            if (port.Contains("CPU", StringComparison.OrdinalIgnoreCase)) continue;
            var type = parts[3].ToLowerInvariant();
            if (type is not ("dynamic" or "static")) continue;
            if (type == "static" && port.Equals("CPU", StringComparison.OrdinalIgnoreCase)) continue;

            map[mac] = port;
        }
        return map;
    }

    /// <summary>Parses <c>show lldp neighbor-information</c> into what is on each port. LLDP is the switch
    /// saying which neighbour it can see where – the strongest evidence there is for a physical link.</summary>
    /// <returns>Port → (neighbour MAC, neighbour name, its port).</returns>
    public static Dictionary<string, (string Mac, string Name, string PortId)> ParseLldpNeighbours(string output)
    {
        var map = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = StripAnsi(raw).Trim();
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Port, Device ID, Port ID, management address, Port Description, System Name
            if (parts.Length < 4) continue;
            if (!parts[0].Contains('/')) continue;              // not a port row (header / rule line)
            var mac = NormaliseMac(parts[1]);
            if (mac.Length == 0) continue;
            map[parts[0]] = (mac, parts[^1], parts[2]);
        }
        return map;
    }

    /// <summary>Reads the percentage out of <c>show cpu-utilization</c> / <c>show memory-utilization</c>
    /// ("   1   |      3%            2%            7%" → 3). The <b>first</b> percentage is the most recent
    /// window; -1 when the output carries none.</summary>
    public static int ParseUtilisation(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = StripAnsi(raw);
            var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})\s*%");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct) && pct <= 100) return pct;
        }
        return -1;
    }

    /// <summary>Parses <c>show logging buffer</c> into the same <see cref="LogEntry"/> shape the RouterOS
    /// reader produces, so one log view can serve any vendor.
    ///
    /// <para>Real lines look like:
    /// <code>#2006-05-14 02:35:50,[Link]/5/Gi1/0/4 changed state to up.</code>
    /// i.e. <c>#date time,[topic]/severity/message</c>, where severity is the numeric syslog level
    /// (3 error, 4 warning, 5 notice, 6 info). Topic and level are joined into the Topics column, which is
    /// what the RouterOS reader puts there too.</para>
    ///
    /// <para>⚠️ The timestamp is passed through <b>verbatim</b>, not re-based on the local clock. These
    /// switches have no RTC, and the one this was measured against reports 2006 – showing the device's own
    /// idea of the time is honest, whereas quietly substituting "now" would hide an unset clock, which is
    /// exactly the thing worth noticing about a log full of 2006.</para></summary>
    public static List<LogEntry> ParseLog(string output, int maxEntries = 500)
    {
        var list = new List<LogEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = StripAnsi(raw).Trim();
            if (!line.StartsWith('#')) continue;                    // prompt, pager, banner
            line = line[1..];

            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            var time = line[..comma].Trim();
            var rest = line[(comma + 1)..].Trim();

            // [Topic]/severity/message – every separator must be present, so a stray line that merely
            // starts with '#' cannot produce a half-filled row.
            if (!rest.StartsWith('[')) continue;
            var close = rest.IndexOf(']');
            if (close <= 0) continue;
            var topic = rest[1..close];
            var after = rest[(close + 1)..];
            if (!after.StartsWith('/')) continue;
            var sevEnd = after.IndexOf('/', 1);
            if (sevEnd <= 0) continue;
            var message = after[(sevEnd + 1)..].Trim();
            if (message.Length == 0) continue;

            list.Add(new LogEntry { Time = time, Topics = $"{topic}/{SeverityName(after[1..sevEnd])}", Message = message });
            if (list.Count >= maxEntries) break;
        }
        return list;
    }

    /// <summary>The syslog level number as a word, so the column reads without a lookup table in the user's
    /// head. An unknown value is passed through unchanged rather than guessed at.</summary>
    private static string SeverityName(string level) => level switch
    {
        "0" => "emergency", "1" => "alert", "2" => "critical", "3" => "error",
        "4" => "warning", "5" => "notice", "6" => "info", "7" => "debug",
        _ => level,
    };

    /// <summary>A MAC in the canonical colon form, or "" when the token is not a MAC. Accepts the colon and
    /// dash spellings the CLI mixes (the table prints colons, system-info prints dashes).</summary>
    private static string NormaliseMac(string token)
    {
        var hex = new string(token.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return "";
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToUpperInvariant();
    }

    /// <summary>Removes the cursor-movement escapes the CLI sprinkles into its output - measured: the first
    /// line of several tables arrives as "ESC[36D Port  Status ...", and left in place the escape glues
    /// itself to the first column so that row parses as garbage.</summary>
    private static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, AnsiPattern, "");

    /// <summary>The escape pattern, built from the ESC code point rather than written as a literal control
    /// character in the source: a raw ESC survives right up until an editor or a diff quietly eats it, and
    /// the stripper would then silently stop stripping.
    ///
    /// <para>⚠️ The ESC is <b>required</b>. It was briefly optional – "so a capture that already lost it
    /// still parses" – and that generosity ate real data: a log line reads
    /// <c>#2006-05-14 02:35:50,[Link]/5/Gi1/0/4 changed state to up.</c>, and <c>[Link]</c> matches
    /// <c>\[[0-9;?]*[A-Za-z]</c> perfectly well without an ESC in front of it. Every log line lost its
    /// topic and the parser returned nothing at all. A bracket is ordinary text; only ESC makes it an
    /// escape sequence.</para></summary>
    private static readonly string AnsiPattern = ((char)27) + @"\[[0-9;?]*[A-Za-z]";}
