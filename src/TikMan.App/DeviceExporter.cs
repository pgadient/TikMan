using TikMan.Core.Storage;

namespace TikMan.App;

/// <summary>Renders the device list to CSV or a self-contained HTML table, for the IPv4 or the IPv6
/// address family. Same columns for both; only the address column differs.
/// <para>This class only decides the <i>columns</i>; the CSV quoting and HTML shell live in
/// <see cref="TabularExport"/> (Core) so the Avalonia client renders byte-identical output.</para></summary>
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
        return TabularExport.ToCsv(
            cols.Select(c => c.Header).ToList(),
            devices.Select(d => (IReadOnlyList<string>)cols.Select(c => c.Get(d)).ToList()));
    }

    public static string ToHtml(IEnumerable<DeviceViewModel> devices, bool ipv6, string title, string generated)
    {
        var cols = Columns(ipv6);
        return TabularExport.ToHtml(
            cols.Select(c => c.Header).ToList(),
            devices.Select(d => (IReadOnlyList<string>)cols.Select(c => c.Get(d)).ToList()),
            title, generated);
    }
}
