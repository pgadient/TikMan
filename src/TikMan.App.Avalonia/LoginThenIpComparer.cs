using System;
using System.Collections;
using System.Net;
using TikMan.Core.Fleet;

namespace TikMan.App.Avalonia;

/// <summary>Numeric comparison of two IP-address strings: parses them and compares byte by byte, so
/// 192.168.2.10 sorts before 192.168.10.2 (and short v6 before long) instead of lexically. Blank/unparseable
/// addresses sort last. Shared by every IP sort here.</summary>
internal static class IpSort
{
    public static int Compare(string a, string b)
    {
        var pa = IPAddress.TryParse(a, out var ia);
        var pb = IPAddress.TryParse(b, out var ib);
        if (pa && pb)
        {
            var ba = ia!.GetAddressBytes();
            var bb = ib!.GetAddressBytes();
            if (ba.Length != bb.Length) return ba.Length - bb.Length;   // v4 before v6
            for (int i = 0; i < ba.Length; i++)
                if (ba[i] != bb[i]) return ba[i] - bb[i];
            return 0;
        }
        if (pa != pb) return pa ? -1 : 1;   // a real address before junk/blank
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Sort comparer for the login/key column. Primary key: devices WITH a stored login first, then those
/// without. Secondary key: the address – IPv4 in the main list, this row's IPv6 in the IPv6 view (the row type
/// decides which). Clicking the header again flips the whole order (the DataGrid reverses).</summary>
public sealed class LoginThenIpComparer : IComparer
{
    public static readonly LoginThenIpComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        var (lx, ipx) = Key(x);
        var (ly, ipy) = Key(y);
        if (lx != ly) return lx ? -1 : 1;   // prio 1: login first
        return IpSort.Compare(ipx, ipy);     // prio 2: then by address
    }

    private static (bool HasLogin, string Ip) Key(object? o) => o switch
    {
        DeviceSnapshot d => (d.HasLogin, d.Ip),      // main list → IPv4
        Ipv6Row r => (r.HasLogin, r.Address),        // IPv6 view → this row's own v6 address
        _ => (false, ""),
    };
}

/// <summary>Sorts rows by their IPv4 address, numerically. Works on both grids (the device snapshot and the
/// IPv6 row both expose the device's IPv4).</summary>
public sealed class Ipv4Comparer : IComparer
{
    public static readonly Ipv4Comparer Instance = new();
    public int Compare(object? x, object? y) => IpSort.Compare(V4(x), V4(y));
    private static string V4(object? o) => o switch
    {
        DeviceSnapshot d => d.Ip,
        Ipv6Row r => r.Ip,
        _ => "",
    };
}

/// <summary>Sorts IPv6-view rows by the address the row stands for, numerically.</summary>
public sealed class Ipv6Comparer : IComparer
{
    public static readonly Ipv6Comparer Instance = new();
    public int Compare(object? x, object? y) => IpSort.Compare(Addr(x), Addr(y));
    private static string Addr(object? o) => o is Ipv6Row r ? r.Address : "";
}
