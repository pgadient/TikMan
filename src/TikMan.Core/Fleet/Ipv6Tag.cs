using TikMan.Core.Discovery;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.Core.Fleet;

/// <summary>A scope tag rendered next to a device's IPv6 addresses. Colours follow the same logic as the
/// service badges: green for "reaches the internet", amber for site-private, grey for link-only – so the
/// column reads without having to know the prefixes by heart.</summary>
/// <param name="Text">Short label for the cell.</param>
/// <param name="Colour">Badge background as a hex string.</param>
/// <param name="Tooltip">The prefix this tag stands for, for anyone who wants the detail.</param>
public sealed record Ipv6Tag(string Text, string Colour, string Tooltip)
{
    // ⚠️ Every tag carries a tooltip – an empty one shows nothing on hover, which is worse than no badge
    // at all: the reader sees a label they can't look up.
    public static Ipv6Tag For(Ipv6Kind kind) => kind switch
    {
        Ipv6Kind.Global => new Ipv6Tag(T("Ipv6_Global"), "#2E7D32", "2000::/3 – " + T("Ipv6_GlobalTip")),
        Ipv6Kind.UniqueLocal => new Ipv6Tag(T("Ipv6_Ula"), "#EF6C00", "fc00::/7 – " + T("Ipv6_UlaTip")),
        Ipv6Kind.LinkLocal => new Ipv6Tag(T("Ipv6_LinkLocal"), "#757575", "fe80::/10 – " + T("Ipv6_LinkLocalTip")),
        Ipv6Kind.Deprecated => new Ipv6Tag(T("Ipv6_Deprecated"), "#C62828", "fec0::/10, ::a.b.c.d – " + T("Ipv6_DeprecatedTip")),
        Ipv6Kind.Multicast => new Ipv6Tag(T("Ipv6_Multicast"), "#6A1B9A", "ff00::/8 – " + T("Ipv6_MulticastTip")),
        Ipv6Kind.Loopback => new Ipv6Tag(T("Ipv6_Loopback"), "#546E7A", "::1 – " + T("Ipv6_LoopbackTip")),
        Ipv6Kind.Unspecified => new Ipv6Tag(T("Ipv6_Unspecified"), "#9E9E9E", ":: – " + T("Ipv6_UnspecifiedTip")),
        _ => new Ipv6Tag(T("Ipv6_Other"), "#9E9E9E", T("Ipv6_OtherTip")),
    };
}

/// <summary>One IPv6 address together with its scope tag and the services that answered <b>on it</b>.
///
/// <para><paramref name="Badges"/> is empty both when nothing answered and when the address was never
/// probed (link-local, or no scan has run yet) – the two are distinguished by
/// <paramref name="Probed"/>, because "tested, offers nothing" and "not tested" are different claims and
/// the UI should not present the second as the first.</para></summary>
/// <param name="Facts">What talking to this address itself revealed (name, OS, model, shares …) – see
/// <see cref="Ipv6Facts"/>. Empty until the meta pass has run for it.</param>
public sealed record Ipv6Entry(
    string Address,
    Ipv6Tag Tag,
    IReadOnlyList<TikMan.Core.Discovery.ServiceBadge> Badges,
    bool Probed,
    Ipv6Facts Facts);
