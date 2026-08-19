using System.Text.RegularExpressions;

namespace TikMan.Core.Api;

/// <summary>One row of a RouterOS DHCP server lease table. Carries the two names a lease holds – the
/// owner's manually-set <see cref="Comment"/> (their own label for the device) and the device-reported
/// <see cref="HostName"/> – plus the DHCP vendor <see cref="ClassId"/> (option 60), which is a free OS
/// fingerprint for gear that exposes nothing else ("android-dhcp-16", "MSFT 5.0").</summary>
public sealed record DhcpLease(string Mac, string Address, string HostName, string Comment, string ClassId);

/// <summary>Reads and interprets the RouterOS DHCP lease table. The parser is static and pure so it can be
/// pinned against real device output; it takes the "/ip/dhcp-server/lease print detail" text (the SSH-CLI
/// path). The REST path parses JSON directly in <see cref="RouterOsClient"/>.</summary>
public static partial class DhcpLeases
{
    /// <summary>Parses "/ip/dhcp-server/lease print detail" into one <see cref="DhcpLease"/> per entry.
    /// Each entry begins with an index (optionally a flags letter) and its wrapped continuation lines are
    /// folded in; the manual comment is the ";;; …" text on the index line. Skips entries without a MAC.</summary>
    public static List<DhcpLease> ParseDetail(string text)
    {
        var list = new List<DhcpLease>();
        foreach (var record in SplitRecords(text))
        {
            var mac = Field(record, "mac-address");
            if (mac.Length == 0) continue;
            list.Add(new DhcpLease(
                mac,
                Field(record, "address"),
                Quoted(record, "host-name"),
                Comment(record),
                Quoted(record, "class-id")));
        }
        return list;
    }

    /// <summary>Parses a dnsmasq lease file (<c>/run/dnsmasq.lease</c> on a UniFi OS console – note the singular
    /// "lease") into leases. Each line is "<c>&lt;expiry-epoch&gt; &lt;mac&gt; &lt;ip&gt; &lt;host-name&gt;
    /// &lt;client-id&gt;</c>"; a host-name of <c>*</c> means the client sent none. dnsmasq carries no lease comment
    /// and no DHCP vendor class, so only the host-name (and address) are filled. Pure + pinnable.</summary>
    public static List<DhcpLease> ParseDnsmasq(string text)
    {
        var list = new List<DhcpLease>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !MacToken().IsMatch(parts[1])) continue;   // 2nd column must be a MAC
            var host = parts[3] == "*" ? "" : parts[3];
            list.Add(new DhcpLease(parts[1].ToUpperInvariant(), parts[2], host, "", ""));
        }
        return list;
    }

    /// <summary>Parses a ZLD firewall's <c>show ip dhcp binding</c> table (verified on a USG FLEX 500, ZLD V5.39)
    /// into leases. Columns are "<c>No. Interface IP MAC Reserved(yes/no) Host-Name Expiration-Time</c>" – the
    /// Description wraps onto an indented next line, which is ignored. MAC → IP → host-name; ZLD carries no lease
    /// comment or DHCP vendor class here, so those stay empty. Pure + pinnable.</summary>
    public static List<DhcpLease> ParseZldBinding(string text)
    {
        var list = new List<DhcpLease>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            // "<n> <iface> <ip> <mac> <yes|no> [host-name] <YYYY-MM-DD …>"; the host-name is optional (a client
            // may send none) and is captured non-greedily up to the expiry date.
            var m = Regex.Match(raw,
                @"^\s*\d+\s+\S+\s+(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F:]{17})\s+(?:yes|no)\s+(?:(.*?)\s+)?\d{4}-\d{2}-\d{2}",
                RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            list.Add(new DhcpLease(m.Groups[2].Value.ToUpperInvariant(), m.Groups[1].Value, m.Groups[3].Value.Trim(), "", ""));
        }
        return list;
    }

    /// <summary>A friendly OS name derived from the DHCP vendor class-id (option 60), or "" when it says
    /// nothing about the OS. "android-dhcp-16" → "Android 16" (the number is the Android major version);
    /// "MSFT 5.0"/"MSFT 98" → "Windows" (Microsoft's DHCP vendor class). Everything else (udhcp, dhcpcd,
    /// appliance strings like "Swisscom TV Box IP2000") is a stack/model, not an OS, and returns "".</summary>
    public static string OsFromClassId(string classId)
    {
        if (string.IsNullOrWhiteSpace(classId)) return "";
        var s = classId.Trim();
        var android = AndroidClass().Match(s);
        if (android.Success) return "Android " + android.Groups[1].Value;
        if (s.StartsWith("MSFT", StringComparison.OrdinalIgnoreCase)) return "Windows";
        return "";
    }

    /// <summary>Splits a "print detail" dump into one string per record: a record starts at a line that
    /// begins (after indent) with the entry number and an optional single flag letter, and folds in the
    /// continuation lines that follow.</summary>
    private static IEnumerable<string> SplitRecords(string text)
    {
        var sb = new System.Text.StringBuilder();
        bool started = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (RecordStart().IsMatch(line))
            {
                if (started) { yield return sb.ToString(); sb.Clear(); }
                started = true;
                sb.Append(line);
            }
            else if (started) sb.Append(' ').Append(line.Trim());
        }
        if (started) yield return sb.ToString();
    }

    /// <summary>Value of an unquoted "key=" field up to the next whitespace. The negative lookbehind stops
    /// "address=" matching inside "active-address="/"mac-address=" and "mac-address=" inside
    /// "active-mac-address=".</summary>
    private static string Field(string record, string key)
    {
        var m = Regex.Match(record, $@"(?<![\w-]){Regex.Escape(key)}=(\S+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Value of a quoted "key=&quot;…&quot;" field (host-name/class-id can contain spaces).</summary>
    private static string Quoted(string record, string key)
    {
        var m = Regex.Match(record, $@"(?<![\w-]){Regex.Escape(key)}=""([^""]*)""");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>The manual ";;; …" comment on the entry, captured up to the first "key=" field folded in
    /// from the continuation line (the comment is free text and may contain spaces and a "|").</summary>
    private static string Comment(string record)
    {
        var m = Regex.Match(record, @";;;\s*(.+?)\s+[\w-]+=");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    [GeneratedRegex(@"^\s*\d+\s+\S")]
    private static partial Regex RecordStart();

    [GeneratedRegex(@"^android-dhcp-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AndroidClass();

    [GeneratedRegex(@"^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")]
    private static partial Regex MacToken();
}
