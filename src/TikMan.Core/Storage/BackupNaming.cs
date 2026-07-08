using System.Text;

namespace TikMan.Core.Storage;

/// <summary>Generates descriptive, filesystem-safe names for config backups.</summary>
public static class BackupNaming
{
    /// <summary>Format: &lt;identity-or-board&gt;_&lt;IP&gt;_&lt;timestamp&gt;.rsc,
    /// e.g. "L009UiGS_192.168.14.19_20260704-011530.rsc".</summary>
    public static string SuggestFileName(string identity, string board, string host, DateTime when)
    {
        var label = FirstNonEmpty(identity, board, "mikrotik");
        return $"{Sanitize(label)}_{Sanitize(host)}_{when:yyyyMMdd-HHmmss}.rsc";
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            sb.Append(c == ' ' || invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim('_');
        return result.Length > 0 ? result : "mikrotik";
    }
}
