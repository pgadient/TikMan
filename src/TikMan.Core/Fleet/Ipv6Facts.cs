using System.Collections.Generic;

namespace TikMan.Core.Fleet;

/// <summary>What was learned by talking to <b>one</b> IPv6 address – the v6 counterpart of the enrichment
/// the IPv4 scan does per device.
///
/// <para>⚠️ Measured over the v6 connector, never copied from the device's IPv4 result. That is the whole
/// point of the tab: a device can serve a different web UI, a different SMB identity or a different share
/// set on its v6 address than on its v4 one (dual-stack hosts routinely bind services to one family only),
/// and a firewall may pass on the global address and drop on the ULA. A row filled in from the IPv4 pass
/// would look identical whether or not the address answers at all.</para>
///
/// <para>Every field may be empty: that means "this address did not tell us", not "the device has none".
/// The views fall back to the device-level value and say where it came from.</para></summary>
/// <param name="Probed">Whether the meta pass actually ran for this address. Distinguishes "asked, learned
/// nothing" from "never asked" (no open port, or a link-local without a usable zone).</param>
public sealed record Ipv6Facts(
    bool Probed = false,
    string Name = "",
    string Os = "",
    string Model = "",
    string Vendor = "",
    string Serial = "",
    // ⚠️ No firmware/version field: nothing TikMan can ask an address without credentials reports one, and
    // a column that is always empty reads as "this address has no firmware" rather than "not asked".
    // The version stays a device fact, shown from the device.
    string WebTitle = "",
    IReadOnlyList<string>? Shares = null,
    string SharesStatus = "",
    string ShareHost = "")
{
    public static readonly Ipv6Facts None = new();

    /// <summary>Share names this address served. Never null, so callers need no guard.</summary>
    public IReadOnlyList<string> ShareNames => Shares ?? System.Array.Empty<string>();

    public bool HasShares => ShareNames.Count > 0;

    /// <summary>True when the host refused to list its shares over this address – worth saying, because
    /// otherwise the row reads as though TikMan never asked.</summary>
    public bool SharesDenied => SharesStatus == "denied";

    /// <summary>UNC paths for this address's shares. ⚠️ Built from <see cref="ShareHost"/>, the form the
    /// enumeration actually succeeded with – a raw IPv6 literal is not a legal UNC host (see
    /// <see cref="TikMan.Core.Discovery.SmbShares.UncHost"/>).</summary>
    public IReadOnlyList<ShareLink> ShareLinks =>
        ShareNames.Select(s => new ShareLink(s, $@"\\{ShareHost}\{s}")).ToList();
}
