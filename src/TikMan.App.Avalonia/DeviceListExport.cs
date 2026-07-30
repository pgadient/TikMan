using System;
using System.Collections.Generic;
using System.Linq;
using TikMan.Core.Fleet;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

/// <summary>Renders the device list to CSV or a self-contained HTML table, for the IPv4 or the IPv6 address
/// family – the same columns the WPF client exports, built from the shared <see cref="DeviceSnapshot"/>.
/// The CSV/HTML rendering itself is <see cref="TabularExport"/> in Core, so both clients produce identical
/// files. Headers stay English on purpose: an exported file usually outlives the UI language it came from.</summary>
public static class DeviceListExport
{
    /// <summary>The IPv4 device table. ⚠️ No ipv6 flag any more: the IPv6 export has its own per-address
    /// shape below, so the "join the addresses into one cell" branch this used to carry became unreachable
    /// the moment that landed - and an unreachable branch reads like a second, supported way to do it.
    private static (string Header, Func<DeviceSnapshot, string> Get)[] Columns() =>
        new (string, Func<DeviceSnapshot, string>)[]
        {
            // Every column the IPv4 grid shows, in the same order – the export used to carry only a third
            // of them. Protocols become a space-joined list of the badge names ("http https ssh").
            ("Type", d => d.KindText),
            ("Name", d => d.Name),
            ("IPv4", d => d.Ip),
            ("IPv6", d => d.Ipv6Summary),
            ("Supported protocols", d => string.Join(" ", d.Badges.Select(b => b.Name))),
            ("MAC", d => d.Mac),
            ("MAC vendor", d => d.MacVendor),
            ("Vendor", d => d.Vendor),
            ("Model", d => d.Model),
            ("Serial", d => d.Serial),
            ("OS", d => d.Os),
            ("Version", d => d.Firmware),
            ("Latest version", d => d.LatestVersion),
            // Installed/Update release dropped to match the grid: duplicates of Version and Latest.
            ("CPU", d => d.Cpu),
            ("RAM", d => d.Memory),
            ("Uptime", d => d.Uptime),
            ("Status", d => d.Status),
            ("Login", d => d.HasLogin ? "yes" : ""),
        };

    /// <summary>The IPv6 export: <b>one row per address</b>, matching what the IPv6 view shows.
    ///
    /// <para>⚠️ Not the device-shaped export with the addresses joined into one cell. That is what this used
    /// to produce, and it quietly handed the user a different table from the one on screen – losing exactly
    /// the per-address facts (scope, and which services answered on <i>that</i> address) the view exists
    /// for.</para></summary>
    private static readonly (string Header, Func<(DeviceSnapshot Device, Ipv6Entry Entry), string> Get)[] Ipv6Columns =
    {
        ("Type", r => r.Device.KindText),
        ("Name", r => Prefer(r.Entry.Facts.Name, r.Device.Name)),
        ("IPv6", r => r.Entry.Address),
        ("Scope", r => r.Entry.Tag.Text),
        // Distinguished on purpose: an untested address is not the same claim as one that answered nothing.
        ("Services", r => !r.Entry.Probed ? "not tested"
            : r.Entry.Badges.Count == 0 ? "none"
            : string.Join(" ", r.Entry.Badges.Select(b => b.Name))),
        ("IPv4", r => r.Device.Ip),
        ("MAC", r => r.Device.Mac),
        ("MAC vendor", r => r.Device.MacVendor),
        // ⚠️ The address's own answer where it gave one, the device's only as a fallback – the same rule the
        // view uses (Ipv6Row.Prefer). An export that silently substituted the IPv4 facts would show a
        // fully-described row for an address that in truth answered nothing.
        ("Vendor", r => Prefer(r.Entry.Facts.Vendor, r.Device.Vendor)),
        ("Model", r => Prefer(r.Entry.Facts.Model, r.Device.Model)),
        ("Serial", r => Prefer(r.Entry.Facts.Serial, r.Device.Serial)),
        ("OS", r => Prefer(r.Entry.Facts.Os, r.Device.Os)),
        ("Shares", r => r.Entry.Facts.HasShares ? string.Join(" ", r.Entry.Facts.ShareNames)
            : r.Entry.Facts.SharesDenied ? "denied" : ""),
        ("Version", r => r.Device.Firmware),
        ("Latest version", r => r.Device.LatestVersion),
        // Installed/Update release dropped to match the grid: duplicates of Version and Latest.
        ("CPU", r => r.Device.Cpu),
        ("RAM", r => r.Device.Memory),
        ("Uptime", r => r.Device.Uptime),
        ("Status", r => r.Device.Status),
        ("Login", r => r.Device.HasLogin ? "yes" : ""),
    };

    private static string Prefer(string perAddress, string device) =>
        perAddress.Length > 0 ? perAddress : device;

    private static IEnumerable<(DeviceSnapshot Device, Ipv6Entry Entry)> Ipv6Rows(
        IEnumerable<DeviceSnapshot> devices) =>
        devices.SelectMany(d => d.Ipv6Entries
            .OrderBy(e => e.Address, StringComparer.OrdinalIgnoreCase)
            .Select(e => (d, e)));

    public static string Ipv6ToCsv(IEnumerable<DeviceSnapshot> devices) =>
        TabularExport.ToCsv(
            Ipv6Columns.Select(c => c.Header).ToList(),
            Ipv6Rows(devices).Select(r => (IReadOnlyList<string>)Ipv6Columns.Select(c => c.Get(r)).ToList()));

    public static string Ipv6ToHtml(IEnumerable<DeviceSnapshot> devices, string title, string generated) =>
        TabularExport.ToHtml(
            Ipv6Columns.Select(c => c.Header).ToList(),
            Ipv6Rows(devices).Select(r => (IReadOnlyList<string>)Ipv6Columns.Select(c => c.Get(r)).ToList()),
            title, generated);

    public static string ToCsv(IEnumerable<DeviceSnapshot> devices)
    {
        var cols = Columns();
        return TabularExport.ToCsv(
            cols.Select(c => c.Header).ToList(),
            devices.Select(d => (IReadOnlyList<string>)cols.Select(c => c.Get(d)).ToList()));
    }

    public static string ToHtml(IEnumerable<DeviceSnapshot> devices, string title, string generated)
    {
        var cols = Columns();
        return TabularExport.ToHtml(
            cols.Select(c => c.Header).ToList(),
            devices.Select(d => (IReadOnlyList<string>)cols.Select(c => c.Get(d)).ToList()),
            title, generated);
    }
}
