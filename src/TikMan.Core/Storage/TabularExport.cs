using System.Net;
using System.Text;

namespace TikMan.Core.Storage;

/// <summary>Renders a table of strings to CSV or a self-contained HTML page. UI-free and column-agnostic,
/// so the WPF client and the cross-platform (Avalonia) client share one implementation of the fiddly parts
/// – CSV quoting and HTML escaping – instead of each carrying its own.</summary>
public static class TabularExport
{
    public static string ToCsv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(CsvField)));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(CsvField)));
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows,
        string title, string generated, string itemNoun = "devices")
    {
        var list = rows.ToList();
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append("<title>").Append(HtmlEnc(title)).Append("</title><style>");
        sb.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}");
        sb.Append("h1{font-size:18px;margin:0 0 2px}p.meta{color:#777;margin:0 0 16px;font-size:13px}");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:13px}");
        sb.Append("th,td{border:1px solid #ddd;padding:5px 8px;text-align:left;vertical-align:top}");
        sb.Append("th{background:#f3f3f3;position:sticky;top:0}tr:nth-child(even) td{background:#fafafa}");
        sb.Append("</style></head><body>");
        sb.Append("<h1>").Append(HtmlEnc(title)).Append("</h1>");
        sb.Append("<p class=\"meta\">").Append(HtmlEnc(generated)).Append(" — ").Append(list.Count)
          .Append(' ').Append(itemNoun).Append("</p>");
        sb.Append("<table><thead><tr>");
        foreach (var h in headers) sb.Append("<th>").Append(HtmlEnc(h)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in list)
        {
            sb.Append("<tr>");
            foreach (var cell in row) sb.Append("<td>").Append(HtmlEnc(cell)).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }

    /// <summary>The "generated at" stamp for an export, as an <b>absolute</b> point in time.
    /// <para>⚠️ A bare local <c>yyyy-MM-dd HH:mm</c> is ambiguous the moment the file leaves the machine that
    /// wrote it – "14:32" means nothing without knowing which clock it was read from, and the reader of an
    /// exported inventory is exactly the person who cannot know. So the offset is always spelled out
    /// (<c>UTC+02:00</c>), plus the zone's own name for humans.</para></summary>
    public static string Stamp(DateTimeOffset? when = null)
    {
        var t = when ?? DateTimeOffset.Now;
        var off = t.Offset;
        // "+02:00" / "-05:30" / "Z" – built by hand because "zzz" has no zero-offset special case.
        var offset = off == TimeSpan.Zero
            ? "UTC"
            : $"UTC{(off < TimeSpan.Zero ? '-' : '+')}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00}";
        var zone = TimeZoneInfo.Local;
        // ⚠️ The zone name only belongs here when the offset really is this machine's for that instant.
        // Appending it unconditionally labelled a 12:00 UTC stamp "(W. Europe Daylight Time)" – a name that
        // contradicts the offset printed right next to it.
        var name = zone.GetUtcOffset(t) != off ? ""
            : zone.IsDaylightSavingTime(t) ? zone.DaylightName : zone.StandardName;
        var suffix = name.Length > 0 && !string.Equals(name, offset, StringComparison.OrdinalIgnoreCase)
            ? $" ({name})" : "";
        return t.ToString("yyyy-MM-dd HH:mm") + " " + offset + suffix;
    }

    /// <summary>Quotes a CSV field only when it has to be: a comma, quote or newline in the value. Embedded
    /// quotes are doubled, per RFC 4180 – a value like <c>He said "hi", loudly</c> must survive a round trip.</summary>
    public static string CsvField(string? value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string HtmlEnc(string? value) => WebUtility.HtmlEncode(value ?? "");
}
