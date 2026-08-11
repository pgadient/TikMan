using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Reads a Zyxel switch over its SSH CLI (XGS/GS series, ZyNOS).
/// <para>SSH rather than SNMP on purpose: SNMP is a service you have to switch on, and plenty of
/// switches ship with it off — the XGS1930 this was written against says <c>no service-control snmp</c>
/// in its own config. SSH is how you already reach the box.</para>
/// <para>And rather than ZON: ZON is raw Ethernet, so it needs Npcap and the same layer-2 segment.
/// <c>show system-information</c> returns the same model and firmware string over plain IP — through a
/// VPN, from another subnet, with nothing installed. ZON keeps its place for devices with no login;
/// for one that has a login, this is strictly better.</para>
/// <para>⚠️ The CLI is read-only on this series: <c>show</c> works, configuring doesn't (that's the web
/// UI). Fine here – TikMan only reads. And no abbreviations: <c>show run</c> answers "invalid command
/// run", it has to be <c>show running-config</c>.</para></summary>
public static partial class ZyxelSsh
{
    /// <summary>What a Zyxel switch says about itself.</summary>
    public sealed record ZyxelInfo(string Model, string Name, string Firmware, string Serial,
        string MacAddress, string HardwareVersion, string Uptime);

    private static ConnectionInfo Info(string host, int port, string user, string password) =>
        new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
            new PasswordAuthenticationMethod(user, password)) { Timeout = TimeSpan.FromSeconds(12) }
            .WithCompatibleMacs();   // Zyxel miscomputes the encrypt-then-MAC variants

    /// <summary>Runs one CLI command over an <b>interactive shell with a PTY</b> and returns its output.
    ///
    /// <para>⚠️ Deliberately NOT the SSH exec channel, and this was MEASURED. The old embedded sshd on this
    /// family (OpenSSH 3.9p1) has a broken exec handler: on an XGS1930 an exec request yields nothing at
    /// all, and on a GS1920 (ZyNOS V4.50) it is <b>fatal</b> – the server splits the command on spaces,
    /// rejects a fragment ("invalid command system-information") and closes the whole connection. The old
    /// design tried exec first and, when it failed, ran the shell on the <i>same</i> connection – which
    /// exec had just torn down, so every read on a GS1920 came back null and the switch looked unreachable.
    /// The CLI on this series is written for a terminal; the shell is the only transport it truly supports,
    /// so it is the only one used.</para>
    ///
    /// <para>The shell path types the command like a human, feeds the pager a space whenever it stalls on
    /// "More", and <see cref="CleanShellOutput"/> strips echo/prompt/control characters so the parsers see
    /// clean text. <paramref name="onError"/> gets a short reason on failure – never the password.</para></summary>
    private static async Task<string?> RunAsync(string host, int port, string user, string password,
        string command, CancellationToken ct, TimeSpan? timeout = null, Action<string>? onError = null)
    {
        var r = await RunManyAsync(host, port, user, password, new[] { command }, ct,
            timeout ?? TimeSpan.FromSeconds(30), onError).ConfigureAwait(false);
        if (r is not null && r[0] is null) onError?.Invoke("SSH connected, but the CLI produced no output");
        return r?[0];
    }

    /// <summary>Runs SEVERAL CLI commands over ONE held, serialized SSH session, returning one cleaned output
    /// per command (null where a command produced nothing).
    ///
    /// <para>⚠️ Through the <see cref="SshSessionPool"/>: the connection is opened once per device and kept,
    /// and the whole read is serialized behind any other read to the same box. Opening a fresh connection per
    /// command means a fresh key-exchange per command, and on this hardware that is both slow (1–2 s of
    /// handshake on a Zyxel, more on a TP-Link) and – on a 2009 GS2200 with 64 MB RAM – a hazard: several
    /// near-simultaneous KEXs, especially alongside an open HTTPS web session, can exhaust the embedded SSH
    /// server and fault it ("newkeys: no keys for mode 0" → Data abort → reboot). One held, serialized
    /// session pays the handshake once and keeps TikMan the lightest possible guest.</para>
    ///
    /// <para>⚠️ The persistent session captures the FIRST caller's credentials; a credential change must
    /// <see cref="InvalidateSession"/> the device so the next read rebuilds with the new ones.</para></summary>
    private static async Task<string?[]?> RunManyAsync(string host, int port, string user, string password,
        IReadOnlyList<string> commands, CancellationToken ct, TimeSpan? timeout = null, Action<string>? onError = null)
    {
        try
        {
            var session = SshSessionPool.GetOrCreate(SshSessionPool.KeyFor(host, port),
                () => new SshSession(() => Info(host, port, user, password), OpenShell));
            var limit = timeout ?? TimeSpan.FromSeconds(45);
            return await session.RunAsync(shell =>
            {
                // Deadline computed HERE, after the queue wait – a session busy with another read must not
                // eat this read's time budget before it even starts.
                var deadline = DateTime.UtcNow + limit;
                var outs = new string?[commands.Count];
                for (int i = 0; i < commands.Count; i++) outs[i] = ReadOneCommand(shell, commands[i], deadline);
                return outs;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { onError?.Invoke(ex.Message); return null; } // SSH off / bad creds / not a Zyxel
    }

    /// <summary>Drops the held SSH session to a device – call when its credentials change, so the next read
    /// rebuilds the session (and its login) with the new ones instead of reusing the stale connection.</summary>
    public static void InvalidateSession(string host, int port) =>
        SshSessionPool.Invalidate(SshSessionPool.KeyFor(host, port));

    /// <summary>Opens and readies a Zyxel shell on a freshly connected client: a wide "dumb" PTY (so long
    /// config lines don't wrap and the pager stays quiet), then swallow the banner/first prompt and turn the
    /// pager off ("terminal length 0"; if a firmware lacks it the error flushes with the banner and harms
    /// nothing). Run once per (re)connect by <see cref="SshSession"/>, not per command.</summary>
    private static Renci.SshNet.ShellStream OpenShell(SshClient ssh)
    {
        var shell = ssh.CreateShellStream("dumb", 512, 60, 4096, 480, 65536);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        DrainUntilIdle(shell, deadline, TimeSpan.FromMilliseconds(900));
        shell.WriteLine("terminal length 0");
        DrainUntilIdle(shell, deadline, TimeSpan.FromMilliseconds(700));
        return shell;
    }

    /// <summary>Types one command and reads its output off the shell until the CLI prompt returns (or the
    /// stream falls quiet), feeding the pager a space whenever it stalls. Returns the cleaned output, or null
    /// when the command produced nothing. Leaves the shell sitting at the prompt, ready for the next
    /// command.</summary>
    private static string? ReadOneCommand(Renci.SshNet.ShellStream shell, string command, DateTime deadline)
    {
        shell.WriteLine(command);
        var sb = new StringBuilder();
        var lastData = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            var chunk = shell.Read();
            if (!string.IsNullOrEmpty(chunk))
            {
                sb.Append(chunk);
                lastData = DateTime.UtcNow;
                if (PagerPrompt().IsMatch(Tail(sb))) shell.Write(" ");
                continue;
            }
            var idleMs = (DateTime.UtcNow - lastData).TotalMilliseconds;
            // Prompt back and briefly quiet = done; otherwise a longer quiet spell also ends it.
            if (sb.Length > 0 && idleMs > (TrailingPrompt().IsMatch(Tail(sb)) ? 350 : 2000)) break;
            Thread.Sleep(80);
        }
        var text = CleanShellOutput(sb.ToString(), command);
        return text.Length > 0 ? text : null;
    }

    private static void DrainUntilIdle(Renci.SshNet.ShellStream shell, DateTime deadline, TimeSpan idle)
    {
        var lastData = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline && DateTime.UtcNow - lastData < idle)
        {
            if (shell.Read() is { Length: > 0 }) lastData = DateTime.UtcNow;
            else Thread.Sleep(60);
        }
    }

    private static string Tail(StringBuilder sb) =>
        sb.Length <= 96 ? sb.ToString() : sb.ToString(sb.Length - 96, 96);

    // The shapes ZyNOS-family pagers stall on. Two families, measured on two firmwares:
    //   XGS1930 (V5.00): "--More--" (optionally in ANSI inverse video), at the end of the line.
    //   GS1920  (V4.50): "-- more --, next page: Space, continue: c, quit: ESC" – the marker is NOT at the
    //                    end of the line, so the old end-anchored pattern never matched it. Only TERM=dumb
    //                    suppressing the pager kept the connector working; this makes it robust without
    //                    relying on that.
    // ⚠️ The dash-wrapped "-- more --" and "next page: space" forms are specific enough to match anywhere
    // in the tail safely; the bare "more:" / "press any key" forms stay end-anchored so a config line that
    // merely contains the word "more" cannot be mistaken for a prompt (a stray space fed into the shell
    // would otherwise land in its input).
    [GeneratedRegex(@"(?i)(--\s*more\s*--|next\s*page:\s*space|(?:\bmore\s*[:.]|press any key)\s*$)")]
    private static partial Regex PagerPrompt();

    // A prompt alone on the last line ("XGS1930#", "Switch>") – the CLI is waiting again.
    [GeneratedRegex(@"(?m)^[\w.\-]{1,40}[#>]\s*$(?!\n)")]
    private static partial Regex TrailingPrompt();

    /// <summary>Turns a raw PTY capture into what the exec channel would have printed: resolves
    /// carriage-return overwrites (how the pager erases its own "More" line), applies backspaces,
    /// strips ANSI escape sequences, and drops the echoed command, pager-prompt remnants and the
    /// trailing CLI prompt. Pure, so the smoke test can pin it against a synthetic capture.</summary>
    public static string CleanShellOutput(string raw, string command)
    {
        // ANSI first (colours/inverse video around "More"), then per-line \r-overwrite + \b.
        var noAnsi = AnsiSequence().Replace(raw, "");
        var lines = new List<string>();
        foreach (var line in noAnsi.Split('\n'))
        {
            var resolved = ResolveOverwrites(line);
            if (resolved.TrimEnd().Length == 0) { lines.Add(""); continue; }
            var t = resolved.Trim();
            if (PagerRemnant().IsMatch(t)) continue;         // a pager line that wasn't overwritten
            lines.Add(resolved.TrimEnd());
        }
        // Drop everything up to and including the echoed command (banner fragments may precede it).
        int start = lines.FindIndex(l => l.Trim() == command.Trim());
        if (start >= 0) lines.RemoveRange(0, start + 1);
        // Drop the prompt the CLI printed when it was done (and any blank tail).
        while (lines.Count > 0 &&
               (lines[^1].Trim().Length == 0 || TrailingPrompt().IsMatch(lines[^1].Trim())))
            lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        return string.Join("\r\n", lines);
    }

    /// <summary>One terminal line: '\r' returns the caret to column 0 and what follows overwrites,
    /// '\b' steps back one column. That is exactly how the pager wipes "--More--" before printing the
    /// next page, so resolving it faithfully makes the pager vanish from the capture.</summary>
    private static string ResolveOverwrites(string line)
    {
        var buf = new StringBuilder();
        int col = 0;
        foreach (var c in line)
        {
            switch (c)
            {
                case '\r': col = 0; break;
                case '\b': if (col > 0) col--; break;
                default:
                    if (col < buf.Length) buf[col] = c; else buf.Append(c);
                    col++;
                    break;
            }
        }
        return buf.ToString();
    }

    // Three shapes: a CSI sequence (ESC [ … letter), a charset-select (ESC ( / ) + char), and a two-byte
    // Fe/Fp escape (ESC + one char). ⚠️ The last alternative is why this is here: a GS1920 on ZyNOS V4.50
    // prefixes every command's output with a run of ESC 7 (DECSC, "save cursor") and ends the prompt with
    // one more. The old regex matched only ESC [ … , so those ESC 7 pairs survived and the parser saw a
    // line of literal "7"s glued to the first row of every table. Measured against the real device.
    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b[()][A-Z0-9]|\x1b[78=>cDEHMNO]")]
    private static partial Regex AnsiSequence();

    // A whole pager line that survived (wasn't erased by a \r-overwrite) – dropped so it never reaches a
    // parser. Covers the XGS1930 "--More--" and the GS1920 "-- more --, next page: Space, …" forms.
    [GeneratedRegex(@"(?i)^(--\s*more\s*--.*|more\s*[:.].*|.*next\s*page:\s*space.*|press any key.*)$")]
    private static partial Regex PagerRemnant();

    /// <summary>Model, firmware, serial, MAC and uptime. Null when SSH didn't answer;
    /// <paramref name="onError"/> then says why.</summary>
    public static async Task<ZyxelInfo?> GetInfoAsync(string host, int port, string user, string password,
        CancellationToken ct = default, Action<string>? onError = null)
    {
        var text = await RunAsync(host, port, user, password, "show system-information", ct, onError: onError);
        return text is null ? null : ParseSystemInformation(text);
    }

    /// <summary>The running configuration – this is the config backup. Null when SSH didn't answer.
    /// <para>⚠️ Unlike RouterOS's <c>/export</c>, which hides secrets, this carries
    /// <c>admin-password cipher …</c>. The text is a credential-bearing artefact: never log it.</para></summary>
    public static async Task<string?> GetRunningConfigAsync(string host, int port, string user, string password,
        CancellationToken ct = default, Action<string>? onError = null)
    {
        var text = await RunAsync(host, port, user, password, "show running-config", ct,
            TimeSpan.FromSeconds(60), onError);   // a 52-port switch prints a lot
        if (text is not { Length: > 0 }) return null;
        // A config is recognised by its own text, not the transport: the "Current configuration"
        // banner or the "; Product Name = …" header comment. Anything else is the CLI complaining.
        if (text.Contains("Current configuration", StringComparison.OrdinalIgnoreCase) ||
            ParseConfigHeader(text).Model.Length > 0) return text;
        onError?.Invoke($"device answered, but not with a running-config ({text.Length} chars)");
        return null;
    }

    /// <summary>The forwarding table as MAC → port name, the same shape <see cref="Discovery.SnmpFdb"/>
    /// returns – so the physical topology map takes it without knowing where it came from. Null when SSH
    /// didn't answer.
    /// <para>This is the point of the whole class: the map's evidence for non-MikroTik gear has only
    /// ever come from SNMP, and a switch with SNMP off contributes nothing. Now it does.</para></summary>
    public static async Task<Dictionary<string, string>?> GetFdbAsync(string host, int port, string user,
        string password, CancellationToken ct = default, Action<string>? onError = null)
    {
        // Both reads down ONE SSH session (one handshake). The port names are a best-effort second command
        // on the same connection – the number is a fine label on its own if the switch doesn't answer it.
        var outputs = await RunManyAsync(host, port, user, password,
            new[] { "show mac address-table all", "show interfaces status" }, ct, onError: onError);
        if (outputs is null || outputs[0] is null) return null;
        var macs = outputs[0]!;
        var status = outputs[1];
        var names = status is null ? new Dictionary<string, string>() : ParseInterfaceNames(status);

        var fdb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mac, portId) in ParseMacTable(macs))
            fdb[mac] = names.TryGetValue(portId, out var name) ? name : portId;
        return fdb;
    }

    /// <summary>CPU load, memory and uptime for the monitoring columns and the history chart – the switch
    /// analogue of RouterOS's <c>/system resource</c>. Null when SSH didn't answer.
    /// <para>Three short reads: <c>show cpu-utilization</c> (a "CPU usage status: NN%" headline),
    /// <c>show memory</c> (a total/used byte row) and the uptime from <c>show system-information</c>.</para></summary>
    public static async Task<ResourceInfo?> GetResourceAsync(string host, int port, string user, string password,
        CancellationToken ct = default, Action<string>? onError = null)
    {
        // ⚠️ All three reads down ONE SSH session (one handshake), not three – see RunManyAsync. A monitoring
        // poll used to open three connections every interval; on a fragile old switch that is three chances
        // to fault its SSH stack, and three lots of KEX latency.
        var outputs = await RunManyAsync(host, port, user, password,
            new[] { "show cpu-utilization", "show memory", "show system-information" }, ct, onError: onError);
        if (outputs is null || outputs[0] is null) return null;   // couldn't read ⇒ unreachable / wrong login
        var cpu = outputs[0]!;
        var mem = outputs[1];
        var sys = outputs[2];

        var (total, used) = mem is null ? (0L, 0L) : ParseMemory(mem);
        var info = sys is null ? null : ParseSystemInformation(sys);
        return new ResourceInfo
        {
            CpuLoad = ParseCpuUtilisation(cpu),
            TotalMemory = total,
            FreeMemory = total > 0 ? total - used : 0,
            Uptime = info?.Uptime ?? "",
            Version = info?.Firmware ?? "",
        };
    }

    /// <summary>CPU load out of <c>show cpu-utilization</c>, in two shapes, one number out either way:
    /// <list type="bullet">
    /// <item><b>XGS1930 / GS1920 (ZyNOS V4.50/V5.00):</b> a headline "CPU usage status:  52.78%".</item>
    /// <item><b>GS2200 (ZyNOS V3.80):</b> NO headline percentage – "CPU usage status:" is followed by a
    /// <c>baseline NNN ticks</c> line and a per-second table whose <c>util</c> column is the load each of the
    /// last ~63 seconds. Averaged into one representative figure (the box swings 25↔100% second to second, so
    /// a single sample is noise; the window mean is the honest reading, and it feeds a 30 s-sampled chart).</item>
    /// </list>
    /// Rounded to a whole percent (ResourceInfo carries an int, like RouterOS's cpu-load). 0 when neither shape
    /// is present.</summary>
    public static int ParseCpuUtilisation(string text)
    {
        var m = CpuHeadline().Match(text);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return (int)Math.Round(v);
        return ParseCpuUtilisationTable(text);
    }

    /// <summary>The GS2200 V3.80 fallback: average every <c>NN.dd</c> utilisation reading in the per-second
    /// table. The percentages are the only two-decimal tokens in that output (the sec/ticks columns are plain
    /// integers, the baseline count too), so matching <c>\d+\.\d\d</c> after the "CPU usage status:" line and
    /// averaging is enough. 0 when the table isn't there. Pure, so the smoke test pins it.</summary>
    public static int ParseCpuUtilisationTable(string text)
    {
        double sum = 0; int n = 0;
        foreach (Match hit in CpuTablePercent().Matches(text))
            if (double.TryParse(hit.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var p))
            { sum += p; n++; }
        return n == 0 ? 0 : Math.Clamp((int)Math.Round(sum / n), 0, 100);
    }

    /// <summary>Total and used bytes out of <c>show memory</c> (shape verbatim from a GS1920):
    /// <code>
    ///   Name          Total          Used     Util
    ///   ------    -----------    ----------    -----
    ///   common    30612608(B)    5954272(B)    19(%)
    /// </code>
    /// The "(B)" values are what matters; "(0,0)" when the row isn't there.</summary>
    public static (long Total, long Used) ParseMemory(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var bytes = MemoryBytes().Matches(raw);
            if (bytes.Count >= 2 &&
                long.TryParse(bytes[0].Groups[1].Value, out var total) &&
                long.TryParse(bytes[1].Groups[1].Value, out var used))
                return (total, used);
        }
        return (0, 0);
    }

    [GeneratedRegex(@"CPU usage status:\s*([\d.]+)\s*%", RegexOptions.IgnoreCase)]
    private static partial Regex CpuHeadline();

    // A two-decimal utilisation reading in the V3.80 per-second table, e.g. " 25.49" or "100.00". The
    // surrounding sec/ticks columns are integers with no decimal point, so this only catches the util%.
    [GeneratedRegex(@"(\d+\.\d\d)")]
    private static partial Regex CpuTablePercent();

    [GeneratedRegex(@"(\d+)\(B\)")]
    private static partial Regex MemoryBytes();

    /// <summary>The switch log, newest first. Null when SSH didn't answer.
    /// <para><c>show logging</c> on this family, measured on a GS1920. The log is <b>newest-first</b>
    /// (entry 1 is the most recent), so <paramref name="maxEntries"/> keeps the first N.</para></summary>
    public static async Task<List<LogEntry>?> GetLogAsync(string host, int port, string user, string password,
        int maxEntries = 200, CancellationToken ct = default, Action<string>? onError = null)
    {
        var text = await RunAsync(host, port, user, password, "show logging", ct,
            TimeSpan.FromSeconds(45), onError);   // a full log is long
        return text is null ? null : ParseLog(text, maxEntries);
    }

    /// <summary>Reads the Zyxel-private serial number over SNMP – the one the web GUI shows on its Status
    /// page but the CLI's <c>show system-information</c> omits on older firmware (a GS1920 on V4.50 reports
    /// no serial there, and no <c>show serial-number</c> command exists).
    ///
    /// <para>⚠️ A Zyxel-<b>private</b> OID (<c>1.3.6.1.4.1.890.1.15.3.1.12</c>), not the standard
    /// ENTITY-MIB <c>entPhysicalSerialNum</c> – that one is unpopulated on this firmware (measured). The
    /// exact leaf can differ across the switch families, so this walks the small model-info table
    /// (<c>…890.1.15.3.1</c>) and returns the first serial-shaped value rather than trusting one index.
    /// Best-effort: "" when SNMP is off, the community is wrong, or nothing there looks like a serial.</para></summary>
    public static async Task<string> GetSerialViaSnmpAsync(string host, string community,
        CancellationToken ct = default)
    {
        try
        {
            var rows = await Discovery.SnmpProbe.WalkAsync(host, community,
                new[] { 1, 3, 6, 1, 4, 1, 890, 1, 15, 3, 1 }, maxRows: 64, ct).ConfigureAwait(false);
            if (rows is null) return "";
            foreach (var (_, _, value) in rows)
            {
                var s = Encoding.ASCII.GetString(value).Trim();
                if (LooksLikeSerial(s)) return s;
            }
        }
        catch { /* best-effort: SNMP off, wrong community, unreachable */ }
        return "";
    }

    // A Zyxel serial: a letter, then 10-15 alphanumerics, e.g. "S000A00000000" (shape anonymised). Tight
    // enough that a model name or a firmware string in a neighbouring OID is not mistaken for one.
    private static bool LooksLikeSerial(string s) =>
        s.Length is >= 11 and <= 16 && char.IsLetter(s[0]) && s.All(char.IsLetterOrDigit) &&
        s.Count(char.IsDigit) >= 6;

    // ---- parsers (pure, pinned to real output) ----

    /// <summary>Parses <c>show logging</c>, in two ZyNOS shapes:
    /// <list type="bullet">
    /// <item><b>GS1920 / XGS1930 (V4.50/V5.00), address anonymised:</b>
    /// <code>
    ///     1 Jan 01 01:23:22 IN authentication: SSH user admin login [IP address = 10.0.0.9]
    ///     4 Jan 01 01:21:04 IN system: Save system configuration 1 successfully
    /// </code>
    /// Columns: index, "Mon DD HH:MM:SS", a 2-letter class (IN = info, ER = error, …), category, message.</item>
    /// <item><b>GS2200 (V3.80):</b>
    /// <code>
    ///   858 Thu Jan  1 00:00:14 1970 PINI  INFO  main: system bootup
    ///   862 Thu Jan  1 00:03:38 1970 PP18  WARN  rt_drop_on_vps: target = 0 nmask=32 code=05
    ///   864 Thu Jan  1 00:04:08 1970 PP33 -WARN  SNMP TRAP 26: Event On Trap
    /// </code>
    /// Extra day-of-week and year, a process token (PINI/PP18) and a word-level (INFO/WARN/ERROR, sometimes
    /// dash-glued as "-WARN"). Topics keeps the level + category; the process token is internal noise.</item>
    /// </list>
    /// Both are newest-first, so <paramref name="maxEntries"/> keeps the first N.</summary>
    public static List<LogEntry> ParseLog(string text, int maxEntries = 0)
    {
        var list = new List<LogEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            LogEntry? entry = null;

            var m = LogLine().Match(line);
            if (m.Success)
                // Class + category together: "IN authentication" – the severity is worth keeping.
                entry = new LogEntry
                {
                    Time = m.Groups[1].Value,
                    Topics = (m.Groups[2].Value + " " + m.Groups[3].Value.Trim()).Trim(),
                    Message = m.Groups[4].Value.Trim(),
                };
            else if ((m = DatedLogLine().Match(line)).Success)
                // Level + category: "WARN rt_drop_on_vps"; drop a stray leading dash on the level.
                entry = new LogEntry
                {
                    Time = m.Groups[1].Value.Trim(),
                    Topics = (m.Groups[3].Value.TrimStart('-').Trim() + " " + m.Groups[4].Value.Trim()).Trim(),
                    Message = m.Groups[5].Value.Trim(),
                };

            if (entry is null) continue;
            list.Add(entry);
            if (maxEntries > 0 && list.Count >= maxEntries) break;   // newest first ⇒ keep the first N
        }
        return list;
    }

    [GeneratedRegex(@"^\s*\d+\s+([A-Z][a-z]{2}\s+\d{1,2}\s+\d{1,2}:\d{2}:\d{2})\s+(\S+)\s+([^:]+?):\s*(.*)$")]
    private static partial Regex LogLine();

    // V3.80: index, day-of-week, then "Mon DD HH:MM:SS YYYY", a process token, a word-level (maybe "-WARN"),
    // category up to the first colon, message. The DOW + year distinguish it from the 2-letter-class form
    // above, so the two regexes never both match a line.
    [GeneratedRegex(@"^\s*\d+\s+[A-Z][a-z]{2}\s+([A-Z][a-z]{2}\s+\d{1,2}\s+\d{1,2}:\d{2}:\d{2}\s+\d{4})\s+(\S+)\s+(-?\S+)\s+([^:]+?):\s*(.*)$")]
    private static partial Regex DatedLogLine();

    /// <summary>Parses <c>show mac address-table all</c> (shape verbatim from an XGS1930-52HP,
    /// MACs anonymised):
    /// <code>
    ///   Port      VLAN ID        MAC Address         Type
    ///   51        1              00:15:5d:0a:0b:0c   Dynamic
    ///   21        3              5c:6a:80:aa:bb:cc   Dynamic
    ///   CPU       1              bc:99:11:00:11:22   Static
    /// </code>
    /// Two things the real output taught:
    /// <list type="bullet">
    /// <item><b>CPU is not a port.</b> That row is the switch's own MAC (it matches the Ethernet Address
    /// in <c>show system-information</c>) and is marked Static. Treating it as a port would hang the
    /// switch off itself.</item>
    /// <item><b>A MAC repeats per VLAN.</b> The same host on a trunk port shows up once per VLAN it is
    /// seen in, so the last one wins and the map gets one entry per device, not one per VLAN.</item>
    /// </list></summary>
    public static List<(string Mac, string Port)> ParseMacTable(string text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var (port, mac) = (parts[0], parts[2]);
            if (!LooksLikeMac(mac)) continue;                                  // header and stray lines
            if (port.Equals("CPU", StringComparison.OrdinalIgnoreCase)) continue;  // the switch itself
            list.Add((mac.ToLowerInvariant(), port));
        }
        return list;
    }

    /// <summary>Parses the port names out of <c>show interfaces status</c>:
    /// <code>
    ///   Port      Name           Link          State         Type       Up Time
    ///   ---- ------------- --------------- ----------- --------------- ----------
    ///     30                       1000M/F  FORWARDING    10/100/1000M    0:39:26
    /// </code>
    /// Number → name, but only for a port that actually carries a label.
    ///
    /// <para>⚠️ Parsed by COLUMN, not by whitespace, and this was measured. On a GS1920 the ports are
    /// unnamed, so the Name column is blank – and a naive whitespace split then reads the <i>Link</i> value
    /// ("1000M/F") as the name and stamps it on the map. The <c>----</c> rule line gives the exact column
    /// spans, so a blank Name column stays blank and the port keeps its number. When no rule line is present
    /// (an unexpected firmware) it falls back to whitespace, but rejects a "name" that is really a
    /// link/state token.</para></summary>
    public static Dictionary<string, string> ParseInterfaceNames(string text)
    {
        var lines = text.Split('\n');
        var spans = lines.Select(RuleColumns).FirstOrDefault(s => s is { Count: >= 2 });

        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (spans is not null)
            {
                var port = Slice(line, spans[0]).Trim();
                if (!int.TryParse(port, out _)) continue;                  // header / rule / stray line
                var name = Slice(line, spans[1]).Trim();
                if (name.Length > 0) d[port] = name;                       // blank column ⇒ keep the number
            }
            else
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[0], out _)) continue;
                if (!LooksLikeLinkOrState(parts[1])) d[parts[0]] = parts[1];
            }
        }
        return d;
    }

    /// <summary>The character spans of a ZyNOS table's columns, read off its <c>---- ---- ----</c> rule
    /// line: each run of dashes is one column, returned as (start, length). Null when the line is not a rule
    /// line (anything other than dashes and spaces, or fewer than two dash runs).</summary>
    private static List<(int Start, int Length)>? RuleColumns(string raw)
    {
        var line = raw.TrimEnd('\r');
        if (line.Trim().Length == 0 || line.Any(c => c != '-' && c != ' ')) return null;
        var spans = new List<(int, int)>();
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '-')
            {
                int start = i;
                while (i < line.Length && line[i] == '-') i++;
                spans.Add((start, i - start));
            }
            else i++;
        }
        return spans.Count >= 2 ? spans : null;
    }

    /// <summary>A column substring, clamped to the line – a data row can be shorter than the rule line, and
    /// the last column often runs a little past its dashes, so the end is not trusted blindly.</summary>
    private static string Slice(string line, (int Start, int Length) span)
    {
        if (span.Start >= line.Length) return "";
        var end = Math.Min(line.Length, span.Start + span.Length);
        return line[span.Start..end];
    }

    // "Down", "STOP", "1000M/F", "1G/F", "100/1000M" – a link/state token, never a user's port label.
    private static bool LooksLikeLinkOrState(string token) =>
        token is "Down" or "STOP" or "FORWARDING" or "DISABLED" ||
        (token.Length > 0 && token.All(c => char.IsDigit(c) || c is '/' or 'M' or 'G' or 'F' or 'H'));

    private static bool LooksLikeMac(string s) =>
        s.Length == 17 && s[2] == ':' && s[5] == ':' && s.Count(c => c == ':') == 5;

    /// <summary>Parses <c>show system-information</c> (shape verbatim from an XGS1930-52HP on
    /// ZyNOS V5.00, serial and MAC anonymised):
    /// <code>
    /// Product Model           : XGS1930-52HP
    /// System Name             : XGS1930
    /// System up Time          :   194:30:49 (29bcf6c1 ticks)
    /// Ethernet Address        : bc:99:11:00:11:22
    /// ZyNOS F/W Version       : V5.00(ABHV.0) | 06/27/2025
    /// Hardware Version        : V1.4
    /// Serial Number           : S231L40001234
    /// </code>
    /// The keys are padded out to a column, and the MAC's own colons sit in the value – so the split is
    /// on the <b>first</b> colon only, which is what <see cref="ParseColonBlock"/> does.</summary>
    public static ZyxelInfo ParseSystemInformation(string text)
    {
        var d = ParseColonBlock(text);
        string Get(string key) => d.TryGetValue(key, out var v) ? v : "";
        // "194:30:49 (29bcf6c1 ticks)" – the ticks are the same number in hex and add nothing.
        var uptime = Get("System up Time");
        if (uptime.IndexOf('(') is > 0 and var paren) uptime = uptime[..paren].Trim();
        return new ZyxelInfo(
            Model: Get("Product Model"),
            Name: Get("System Name"),
            Firmware: Get("ZyNOS F/W Version"),
            Serial: Get("Serial Number"),
            MacAddress: Get("Ethernet Address"),
            HardwareVersion: Get("Hardware Version"),
            Uptime: uptime);
    }

    /// <summary>Model and firmware straight off the <c>show running-config</c> header:
    /// <code>
    /// ; Product Name = XGS1930-52HP
    /// ; Firmware Version = V5.00(ABHV.0) | 06/27/2025
    /// </code>
    /// A second source for the same two facts, free with a backup – so a device that has just been
    /// backed up needn't be asked again.</summary>
    public static (string Model, string Firmware) ParseConfigHeader(string config)
    {
        string model = "", firmware = "";
        foreach (var raw in config.Split('\n').Take(20))   // the header is the first few lines
        {
            var line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith(';')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[1..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key.Equals("Product Name", StringComparison.OrdinalIgnoreCase)) model = val;
            else if (key.Equals("Firmware Version", StringComparison.OrdinalIgnoreCase)) firmware = val;
        }
        return (model, firmware);
    }

    /// <summary>"Key : value", split on the first colon. Not RouterOsSsh.ParseColon: that one trims the
    /// key of a right-aligned block, and here the value can itself contain colons (the MAC).</summary>
    public static Dictionary<string, string> ParseColonBlock(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            var key = line[..c].Trim();
            if (key.Length > 0 && !d.ContainsKey(key)) d[key] = line[(c + 1)..].Trim();
        }
        return d;
    }
}
