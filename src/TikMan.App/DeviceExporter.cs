using System.Net;
using System.Text;

namespace TikMan.App;

/// <summary>Renders the device list to CSV or a self-contained HTML table, for the IPv4 or the IPv6
/// address family. Same columns for both; only the address column differs.</summary>
public static class DeviceExporter
{
    private static (string Header, Func<DeviceViewModel, string> Get)[] Columns(bool ipv6) => new (string, Func<DeviceViewModel, string>)[]
    {
        ("Type", d => d.DeviceType),
        ("Name", d => d.Name),
        (ipv6 ? "IPv6" : "IPv4", d => ipv6 ? string.Join("; ", d.Ipv6List) : d.Ipv4Address),
        ("Services", d => string.Join(", ", d.SupportedProtocols.Select(p => p.Name))),
        ("MAC", d => d.Model.MacAddress),
        ("MAC vendor", d => d.MacVendor),
        ("Vendor", d => d.IdentifiedVendor),
        ("Model", d => d.ModelDisplay),
        ("Serial", d => d.SerialNumber),
        ("OS", d => d.OsDisplay),
        ("Version", d => d.Version),
        ("Uptime", d => d.Uptime),
    };

    public static string ToCsv(IEnumerable<DeviceViewModel> devices, bool ipv6)
    {
        var cols = Columns(ipv6);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(c => CsvField(c.Header))));
        foreach (var d in devices)
            sb.AppendLine(string.Join(",", cols.Select(c => CsvField(c.Get(d)))));
        return sb.ToString();
    }

    public static string ToHtml(IEnumerable<DeviceViewModel> devices, bool ipv6, string title, string generated)
    {
        var cols = Columns(ipv6);
        var list = devices.ToList();
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
        sb.Append("<p class=\"meta\">").Append(HtmlEnc(generated)).Append(" — ").Append(list.Count).Append(" devices</p>");
        sb.Append("<table><thead><tr>");
        foreach (var c in cols) sb.Append("<th>").Append(HtmlEnc(c.Header)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var d in list)
        {
            sb.Append("<tr>");
            foreach (var c in cols) sb.Append("<td>").Append(HtmlEnc(c.Get(d))).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string HtmlEnc(string value) => WebUtility.HtmlEncode(value ?? "");
}
