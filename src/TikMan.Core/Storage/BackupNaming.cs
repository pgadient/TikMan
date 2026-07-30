using System.Text;

namespace TikMan.Core.Storage;

/// <summary>Generates descriptive, filesystem-safe names for config backups. Safe means safe on
/// every OS the file may travel to – Windows is the strictest (reserved characters, reserved device
/// names like CON/COM1, no trailing dot), macOS adds ':' and Linux '/', both inside the Windows set –
/// and never longer than 50 characters, so a device with a baroque identity can't produce a name
/// that breaks path-length limits or sync tools.</summary>
public static class BackupNaming
{
    private const int MaxLength = 50;

    // Windows refuses these as base names regardless of extension ("CON.rsc" is still CON).
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Format: &lt;identity-or-board&gt;_&lt;IP&gt;_&lt;timestamp&gt;&lt;ext&gt;,
    /// e.g. "RouterA_192.0.2.10_20260704-011530.rsc" – capped at 50 characters by trimming the
    /// label first (it is the decorative part) and the host second (the timestamp stays, it is what
    /// keeps successive backups distinct). The extension names the dialect: ".rsc" for a RouterOS
    /// export, ".cfg" for a Zyxel running-config.</summary>
    public static string SuggestFileName(string identity, string board, string host, DateTime when,
        string ext = ".rsc")
    {
        var stamp = $"{when:yyyyMMdd-HHmmss}";
        var label = Sanitize(FirstNonEmpty(identity, board, "mikrotik"), fallback: "mikrotik");
        var hostPart = Sanitize(host, fallback: "");

        int Budget() => MaxLength - ext.Length - stamp.Length - 1     // "_<stamp>.rsc"
                        - (hostPart.Length > 0 ? hostPart.Length + 1 : 0);
        if (label.Length > Budget())
        {
            // The label yields first, but keeps at least 8 characters of identity …
            // ⚠️ …and never more than it has. The floor of 8 is a *desired* length, not a promise the
            // label is that long: with a long host (an IPv6 address sanitises to 39 characters) the
            // budget goes negative, and a short RouterOS identity – "GW", "sw1", "hAP" are the norm –
            // then made this slice past the end of the string and take the whole backup down.
            label = label[..Math.Min(label.Length, Math.Max(8, Math.Max(0, Budget())))]
                .TrimEnd('_', '-', '.');
            // … after which an overlong host (IPv6) yields the rest.
            if (label.Length > Budget() && hostPart.Length > 0)
            {
                int hostMax = Math.Max(0, MaxLength - ext.Length - stamp.Length - 2 - label.Length);
                hostPart = hostMax == 0 ? "" : hostPart[..Math.Min(hostPart.Length, hostMax)].TrimEnd('_', '-', '.');
            }
        }

        var name = hostPart.Length > 0 ? $"{label}_{hostPart}_{stamp}" : $"{label}_{stamp}";
        return name + ext;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>One filename segment: every character Windows/macOS/Linux won't take (or that shells
    /// dislike) becomes '_', runs collapse, and edges lose the fillers. Reserved Windows device names
    /// get a suffix so "CON" can never become a base name.</summary>
    private static string Sanitize(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();   // superset: covers '/' (Linux) and ':' (macOS)
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (char.IsControl(c)) continue;
            sb.Append(c == ' ' || invalid.Contains(c) ? '_' : c);
        }
        // Collapse runs of '_' – "a  b" and "a?*b" both read "a_b", not "a___b".
        var collapsed = new StringBuilder(sb.Length);
        foreach (var c in sb.ToString())
            if (c != '_' || collapsed.Length == 0 || collapsed[^1] != '_') collapsed.Append(c);

        var result = collapsed.ToString().Trim('_', '.', '-');
        if (result.Length == 0) return fallback;
        if (ReservedNames.Contains(result)) result += "-dev";
        return result;
    }
}
