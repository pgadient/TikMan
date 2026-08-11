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
    /// <summary>What we show in the list for a ZLD firewall. <paramref name="MacRange"/> is the device's own
    /// MAC block as "first-last" (see <see cref="ParseMacRange"/>) – "" when the read failed.</summary>
    public readonly record struct ZldInfo(string Model, string Firmware, string Serial, string MacRange = "");

    /// <summary>Model + running firmware + serial + own MAC block, or null when the device could not be read
    /// (offline, wrong login, or not actually a ZLD box). Used to fill the row after a login is set.</summary>
    public static async Task<ZldInfo?> GetInfoAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show version", "show serial-number", "show mac" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 1) return null;
        var (model, firmware, _) = ParseVersion(outputs[0]);
        var serial = outputs.Count > 1 ? ParseSerial(outputs[1]) : "";
        var macRange = outputs.Count > 2 ? ParseMacRange(outputs[2]) : "";
        // ⚠️ A ZLD read that yields neither model nor firmware is not a ZLD device (or the command errored) –
        // report failure rather than a blank record, so a non-firewall that happens to answer SSH is not
        // relabelled a firewall with empty facts.
        if (model.Length == 0 && firmware.Length == 0) return null;
        return new ZldInfo(model, firmware, serial, macRange);
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

    /// <summary>The device's own MAC block out of <c>show mac</c>, normalised to "FIRST-LAST" uppercase
    /// (shape, anonymised: "MAC address: BC:99:11:00:00:0B-BC:99:11:00:00:12"). "" when absent.
    ///
    /// <para>⚠️ Why a RANGE matters: a ZLD firewall owns one MAC <b>per physical port</b>, allocated as a
    /// consecutive block, and its LAN traffic egresses with the <i>port's</i> MAC – NOT the MAC its IP
    /// answers ARP with. So the inventory MAC is in no switch's forwarding table, and the firewall could
    /// never be placed on the physical map. The block is what the switches actually see.</para></summary>
    public static string ParseMacRange(string output)
    {
        var m = Regex.Match(output ?? "",
            @"MAC address:\s*([0-9A-Fa-f:]{17})\s*-\s*([0-9A-Fa-f:]{17})", RegexOptions.IgnoreCase);
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value.ToUpperInvariant()}" : "";
    }

    /// <summary>Which of the firewall's block MACs belongs to which interface (sfp / wan1 / lan1 / …) –
    /// measured, not assumed: each interface group carries ONE MAC from the block (<c>show interface lan1</c>
    /// → "current MAC address: …"), so the MAC a switch learns names the GROUP the firewall is cabled on.
    /// Keys are uppercase colon MACs; empty on failure (best-effort – placement works without the labels).
    /// <para>Two stages down the held session: <c>show port-grouping</c> lists the group names, then one
    /// <c>show interface &lt;name&gt;</c> per group. The "reserved" pseudo-group is skipped.</para></summary>
    public static async Task<Dictionary<string, string>> GetInterfaceMacMapAsync(string host, int port,
        string user, string password, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = await RunAsync(host, port, user, password, new[] { "show port-grouping" }, ct).ConfigureAwait(false);
        if (groups is null || groups.Count < 1) return map;
        var names = ParsePortGroupNames(groups[0]);
        if (names.Count == 0) return map;

        var outputs = await RunAsync(host, port, user, password,
            names.Select(n => $"show interface {n}").ToList(), ct).ConfigureAwait(false);
        if (outputs is null) return map;
        foreach (var o in outputs)
        {
            var (name, mac) = ParseInterfaceNameMac(o);
            if (name.Length > 0 && mac.Length > 0) map[mac] = name;
        }
        return map;
    }

    /// <summary>The group names out of <c>show port-grouping</c> (rows "1   sfp   yes no …" – the second
    /// column). "reserved" is an unused pseudo-group and is skipped.</summary>
    public static List<string> ParsePortGroupNames(string output)
    {
        var list = new List<string>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out _)) continue;
            if (parts[1].Equals("reserved", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(parts[1]);
        }
        return list;
    }

    /// <summary>Interface name + current MAC out of one <c>show interface &lt;name&gt;</c> block
    /// ("interface name: lan1" / "current MAC address: BC:99:11:00:00:0E" – anonymised).</summary>
    public static (string Name, string Mac) ParseInterfaceNameMac(string output)
    {
        var name = Regex.Match(output ?? "", @"interface name:\s*(\S+)", RegexOptions.IgnoreCase);
        var mac = Regex.Match(output ?? "", @"current MAC address:\s*([0-9A-Fa-f:]{17})", RegexOptions.IgnoreCase);
        return (name.Success ? name.Groups[1].Value.Trim() : "",
                mac.Success ? mac.Groups[1].Value.ToUpperInvariant() : "");
    }

    /// <summary>The firewall's L3 adjacency – who talks to it, per interface – as (MAC, Interface) pairs from
    /// <c>show arp-table</c>. Empty when unreadable.
    /// <para>⚠️ This is NOT a forwarding table and must never be fed into the map as one (measured 2026-08-01:
    /// doing so pulled every ARPed host under the firewall). It is the WITNESS HALF of the shared-witness
    /// placement: the firewall claims "host H is adjacent to me on lan1", the switches prove where H really
    /// attaches – and if all witnesses attach at one switch port, the firewall sits between them and that
    /// port. Needed because a firewall whose only LAN peer hangs BEHIND it is invisible to every switch
    /// (its frames are switched locally and never cross the uplink – measured on the live net).</para></summary>
    public static async Task<List<(string Mac, string Label)>> GetAdjacencyAsync(string host, int port,
        string user, string password, CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show arp-table", "show port-grouping", "show port status", "show zon lldp neighbors" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 1) return new List<(string, string)>();
        var arp = ParseArpTable(outputs[0]);
        var groupPorts = outputs.Count > 1 ? ParsePortGroupPortList(outputs[1]) : new Dictionary<string, List<int>>();
        var active = outputs.Count > 2 ? ParsePortStatus(outputs[2]) : new HashSet<int>();
        // The uplink physical ports per group: a group port that HEARS an LLDP neighbour (a switch/router) is
        // an uplink, not a client port. Re-reading LLDP here (also read for placement) is one cheap command on
        // the warm pooled session. Empty when LLDP is off ⇒ the label just isn't narrowed by uplink.
        var uplink = outputs.Count > 3 ? UplinkPortsByGroup(ParseLldpNeighbors(outputs[3])) : new Dictionary<string, HashSet<int>>();
        return arp.Select(a => (a.Mac, PortGroupLabel(a.Iface, groupPorts, active, uplink))).ToList();
    }

    /// <summary>The label for a host on interface group <paramref name="group"/>. The ZLD exposes no
    /// MAC→physical-port map, so we name the ports the host COULD be on – never a wrong single guess – and
    /// narrow that set with what we DO know: drop link-down ports (<c>show port status</c>, no LLDP needed),
    /// then drop the uplink ports (LLDP). Often collapses to the one real client port ("P6 (lan1)"); with
    /// several clients it stays a small honest set. Degrades gracefully to the full group if a read failed.</summary>
    private static string PortGroupLabel(string group, Dictionary<string, List<int>> groupPorts,
        HashSet<int> active, Dictionary<string, HashSet<int>> uplink)
    {
        if (!groupPorts.TryGetValue(group, out var all) || all.Count == 0) return group;   // group unknown
        var sel = new List<int>(all);
        var act = sel.Where(active.Contains).ToList();
        if (act.Count > 0) sel = act;                                     // drop link-down ports (port-status)
        if (uplink.TryGetValue(group, out var up))
        {
            var noUp = sel.Where(p => !up.Contains(p)).ToList();
            if (noUp.Count > 0) sel = noUp;                              // drop the uplink port(s) (LLDP)
        }
        return $"{CompactPorts(sel)} ({group})";
    }

    /// <summary>Interface group → the LLDP-heard (uplink) physical port numbers on it. A firewall port that
    /// hears a named neighbour is an infrastructure uplink, so it is removed from a client's candidate set.</summary>
    private static Dictionary<string, HashSet<int>> UplinkPortsByGroup(List<LldpNeighbor> neighbours)
    {
        var map = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in neighbours)
        {
            var m = Regex.Match(n.LocalPort, @"\d+");                     // "P4" → 4
            if (!m.Success || n.LocalGroup.Length == 0) continue;
            if (!map.TryGetValue(n.LocalGroup, out var set)) map[n.LocalGroup] = set = new HashSet<int>();
            set.Add(int.Parse(m.Value));
        }
        return map;
    }

    /// <summary>The physical ports with link up, from <c>show port status</c> – the Status column carries a
    /// speed/duplex ("1000M/Full") when up and "Down" when not, so a "/" in it means the port is active.
    /// <code>
    /// Port    Status      TxPkts  ...
    /// 4       1000M/Full  ...
    /// 6       Down        ...
    /// </code></summary>
    public static HashSet<int> ParsePortStatus(string output)
    {
        var up = new HashSet<int>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var port)) continue;
            if (parts[1].Contains('/')) up.Add(port);                    // "1000M/Full" up, "Down" not
        }
        return up;
    }

    /// <summary>Interface group → its physical port numbers, from <c>show port-grouping</c> (the "yes"
    /// columns). "reserved" is skipped. See <see cref="ParsePortGroupPorts"/> for the compacted form.</summary>
    public static Dictionary<string, List<int>> ParsePortGroupPortList(string output)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], out _)) continue;
            if (parts[1].Equals("reserved", StringComparison.OrdinalIgnoreCase)) continue;
            var ports = new List<int>();
            for (int i = 2; i < parts.Length; i++)                       // Port1 is at index 2
                if (parts[i].Equals("yes", StringComparison.OrdinalIgnoreCase)) ports.Add(i - 1);
            if (ports.Count > 0) map[parts[1]] = ports;
        }
        return map;
    }

    /// <summary>Interface group → its physical ports, compacted ("lan1" → "P2-P8", "sfp" → "P1").</summary>
    public static Dictionary<string, string> ParsePortGroupPorts(string output) =>
        ParsePortGroupPortList(output).ToDictionary(kv => kv.Key, kv => CompactPorts(kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Collapses a sorted port list into runs: [2..8] → "P2-P8", [2,4,6] → "P2, P4, P6".</summary>
    private static string CompactPorts(List<int> ports)
    {
        ports.Sort();
        var segs = new List<string>();
        int start = ports[0], prev = ports[0];
        for (int i = 1; i <= ports.Count; i++)
        {
            if (i < ports.Count && ports[i] == prev + 1) { prev = ports[i]; continue; }
            segs.Add(start == prev ? $"P{start}" : $"P{start}-P{prev}");
            if (i < ports.Count) start = prev = ports[i];
        }
        return string.Join(", ", segs);
    }

    /// <summary>Parses <c>show arp-table</c> (shape verbatim from a USG FLEX 500, values anonymised):
    /// <code>
    /// Address                  HWtype  HWaddress           Flags Mask
    /// 10.0.0.23                ether   3c:18:a0:aa:bb:cc   C                     lan1
    /// </code>
    /// One (MAC, interface) pair per row; the same MAC under several IPs is reported once. Incomplete
    /// entries (MAC 00:00:…) are skipped.</summary>
    public static List<(string Mac, string Iface)> ParseArpTable(string output)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var mac = parts.FirstOrDefault(p => Regex.IsMatch(p, @"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$"));
            if (mac is null || mac == "00:00:00:00:00:00") continue;
            var iface = parts[^1];
            if (iface.Contains(':')) continue;   // row ended on the MAC – no interface column
            if (seen.Add(mac)) list.Add((mac.ToUpperInvariant(), iface));
        }
        return list;
    }

    /// <summary>One LLDP neighbour the firewall hears: its local port (and interface group) the neighbour is
    /// on, plus the neighbour's system name, IP and port. Empty strings for fields the neighbour did not send
    /// ("N/A").</summary>
    public readonly record struct LldpNeighbor(string LocalPort, string LocalGroup, string Name, string Ip, string Port, string Mac);

    /// <summary>The LLDP neighbour table – DIRECT link evidence with the exact far-end port, unlike ARP. Needs
    /// <c>zon lldp server</c> enabled on the firewall; empty otherwise. LLDP only sees LLDP-capable neighbours
    /// (switches, routers, APs) – a plain PC does not announce itself, so it never appears here.</summary>
    public static async Task<List<LldpNeighbor>> GetLldpNeighborsAsync(string host, int port,
        string user, string password, CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show zon lldp neighbors" }, ct).ConfigureAwait(false);
        return outputs is null || outputs.Count < 1 ? new List<LldpNeighbor>() : ParseLldpNeighbors(outputs[0]);
    }

    /// <summary>Parses <c>show zon lldp neighbors</c> (columns Local Port, Model Name, System Name, Firmware
    /// Version, Port, IP, MAC).
    /// <code>
    /// Local Port   Model Name   System Name   Firmware Version   Port      IP             MAC
    /// P4(lan1)     N/A          CoreSwitch01  N/A                Pcombo4   10.0.0.6       N/A   (anonymised)
    /// </code>
    /// ⚠️ TOKEN-based, NOT fixed-width: the live SSH output aligns its VALUES one column off from the header
    /// words (measured – header-position slicing dropped the first char of every field: "CoreSwitch01" →
    /// "oreSwitch01", "10.0.0.6" → "0.0.0.6"). Empty fields come through as "N/A" (never blank), so a
    /// whitespace split yields one token per column. The IP is found by regex and the neighbour port sits
    /// right before it – robust even if an earlier column shifted. Rows whose neighbour is all-N/A are dropped.</summary>
    public static List<LldpNeighbor> ParseLldpNeighbors(string output)
    {
        var list = new List<LldpNeighbor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inTable = false;
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inTable) { if (line.Contains("Local Port") && line.Contains("System Name")) inTable = true; continue; }
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);   // on whitespace
            if (t.Length < 2 || !Regex.IsMatch(t[0], @"^[A-Za-z]*\d+(\(.+\))?$")) continue;  // skip separator/echo rows

            static string Clean(string s) => s.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? "" : s;
            int ipIdx = -1;
            for (int i = 1; i < t.Length; i++)
                if (Regex.IsMatch(t[i], @"^\d{1,3}(\.\d{1,3}){3}$")) { ipIdx = i; break; }

            string name = t.Length > 2 ? Clean(t[2]) : "";
            string port = "", ip = "", mac = "";
            if (ipIdx >= 0)
            {
                ip = t[ipIdx];
                if (ipIdx - 1 >= 1) port = Clean(t[ipIdx - 1]);
                if (ipIdx + 1 < t.Length) mac = Clean(t[ipIdx + 1]);
            }
            else   // no IP column present – fall back to the nominal token positions
            {
                if (t.Length > 4) port = Clean(t[4]);
                if (t.Length > 6) mac = Clean(t[6]);
            }
            port = NormalizeLldpPort(port);
            if (name.Length == 0 && ip.Length == 0 && mac.Length == 0) continue;    // empty neighbour row

            string localPort = t[0], localGroup = "";
            var lm = Regex.Match(t[0], @"^([^()]+)\(([^()]+)\)$");                    // "P4(lan1)" → "P4", "lan1"
            if (lm.Success) { localPort = lm.Groups[1].Value.Trim(); localGroup = lm.Groups[2].Value.Trim(); }
            if (seen.Add(name + "|" + ip + "|" + port))
                list.Add(new LldpNeighbor(localPort, localGroup, name, ip, port, mac.ToUpperInvariant()));
        }
        return list;
    }

    /// <summary>The neighbour's port as LLDP reports it: the ZLD prefixes a bare "P" ("Pcombo4"); strip it
    /// when a letter follows, so it matches the neighbour's own interface name ("combo4") – but keep a purely
    /// numeric port ("P4").</summary>
    private static string NormalizeLldpPort(string p) =>
        p.Length >= 2 && (p[0] == 'P' || p[0] == 'p') && char.IsLetter(p[1]) ? p.Substring(1) : p;

    /// <summary>The WLAN picture of a ZLD box acting as an AP controller: the configured SSIDs, and whether
    /// any AP is actually managed right now. Both best-effort ("" / empty on failure).
    /// <para>⚠️ The SSIDs come from <c>show wlan-ssid-profile all</c> – configuration, which exists even on a
    /// box that has never seen an AP (a factory default profile carries SSID "ZyXEL"). The caller should
    /// therefore only PRESENT the SSIDs when <c>ApsManaged</c> is true, or a plain firewall shows a WLAN it
    /// does not broadcast. The AP check is <c>show capwap ap all</c>: any MAC in its output is a managed AP.
    /// (Verified against a USG FLEX 500 WITHOUT APs – empty AP table, default profile parsed; the with-APs
    /// side follows the same documented table and awaits real hardware.)</para></summary>
    public static async Task<(bool ApsManaged, List<(string Profile, string Ssid)> Ssids)> GetWlanAsync(
        string host, int port, string user, string password, CancellationToken ct = default)
    {
        var outputs = await RunAsync(host, port, user, password,
            new[] { "show capwap ap all", "show wlan-ssid-profile all" }, ct).ConfigureAwait(false);
        if (outputs is null || outputs.Count < 2) return (false, new List<(string, string)>());
        return (ContainsMac(outputs[0]), ParseWlanSsidProfiles(outputs[1]));
    }

    /// <summary>Whether the text contains a MAC-shaped token – the "is any AP managed" test on the
    /// <c>show capwap ap all</c> output (an empty controller prints nothing at all).</summary>
    public static bool ContainsMac(string text) =>
        Regex.IsMatch(text ?? "", @"\b([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\b");

    /// <summary>Parses <c>show wlan-ssid-profile all</c> (shape verbatim from a USG FLEX 500):
    /// <code>
    /// ssid profile: default
    ///   reference: 7
    ///   SSID: ZyXEL
    ///   …
    /// </code>
    /// Records start at "ssid profile: name"; the SSID line inside carries the actual network name.</summary>
    public static List<(string Profile, string Ssid)> ParseWlanSsidProfiles(string output)
    {
        var list = new List<(string, string)>();
        string profile = "";
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var p = Regex.Match(line, @"^ssid profile:\s*(.+)$", RegexOptions.IgnoreCase);
            if (p.Success) { profile = p.Groups[1].Value.Trim(); continue; }
            var s = Regex.Match(line, @"^SSID:\s*(.+)$");
            if (s.Success && profile.Length > 0) list.Add((profile, s.Groups[1].Value.Trim()));
        }
        return list;
    }

    /// <summary>Expands a "FIRST-LAST" MAC range (as <see cref="ParseMacRange"/> returns) into the individual
    /// addresses, inclusive. Empty when the text is not a range, the order is reversed, or the span is
    /// implausibly large (capped at 64 – a firewall has a handful of ports, and a garbled range must not
    /// balloon into millions of entries).</summary>
    public static List<string> ExpandMacRange(string range)
    {
        var list = new List<string>();
        var m = Regex.Match(range ?? "", @"^([0-9A-F:]{17})-([0-9A-F:]{17})$", RegexOptions.IgnoreCase);
        if (!m.Success) return list;
        static ulong Val(string mac) => ulong.Parse(mac.Replace(":", ""), System.Globalization.NumberStyles.HexNumber);
        var first = Val(m.Groups[1].Value);
        var last = Val(m.Groups[2].Value);
        if (last < first || last - first >= 64) return list;
        for (var v = first; v <= last; v++)
            list.Add(string.Join(":", Enumerable.Range(0, 6).Select(i => ((v >> (40 - 8 * i)) & 0xFF).ToString("X2"))));
        return list;
    }

    // ---- transport: interactive shell, one session per call ------------------------------------------

    private static async Task<List<string>?> RunAsync(string host, int port, string user, string password,
        IReadOnlyList<string> commands, CancellationToken ct, TimeSpan? budget = null)
    {
        var per = budget ?? TimeSpan.FromSeconds(30);
        try
        {
            // One held, serialized session per device (see SshSessionPool): pays the handshake once and never
            // opens a second concurrent login. The commands were already batched down one shell; now the
            // shell itself persists across calls too.
            var session = SshSessionPool.GetOrCreate(SshSessionPool.KeyFor(host, port),
                () => new SshSession(() => Info(host, port, user, password), OpenShell));
            return await session.RunAsync(shell =>
            {
                var outputs = new List<string>(commands.Count);
                foreach (var cmd in commands)
                {
                    ct.ThrowIfCancellationRequested();
                    shell.WriteLine(cmd);
                    outputs.Add(ReadUntilPrompt(shell, per));
                }
                return outputs;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }   // unreachable, SSH off, bad login – the caller decides
    }

    private static ConnectionInfo Info(string host, int port, string user, string password) =>
        new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
            new PasswordAuthenticationMethod(user, password)) { Timeout = TimeSpan.FromSeconds(20) }
            .WithCompatibleMacs();   // ZLD miscomputes the encrypt-then-MAC HMACs

    /// <summary>Opens and readies a ZLD shell on a freshly connected client: a wide "vt100" PTY, then swallow
    /// the login banner/first prompt. Run once per (re)connect by <see cref="SshSession"/>, not per command.</summary>
    private static Renci.SshNet.ShellStream OpenShell(SshClient ssh)
    {
        var shell = ssh.CreateShellStream("vt100", 200, 4000, 0, 0, 1 << 20);
        ReadUntilPrompt(shell, TimeSpan.FromSeconds(8));   // login banner + first prompt
        return shell;
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
