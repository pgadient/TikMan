using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace TikMan.Core.Discovery;

/// <summary>Where an IPv6 address is reachable from. The distinction matters when reading a device list:
/// a link-local address is useless outside its own segment, while a global one is routable from anywhere –
/// so "has IPv6" alone says very little.</summary>
public enum Ipv6Kind
{
    /// <summary>Not an IPv6 address (or unparseable).</summary>
    None,
    /// <summary><c>fe80::/10</c> – valid only on the link it was seen on. Every IPv6 host has one.</summary>
    LinkLocal,
    /// <summary><c>fc00::/7</c> – unique local, the IPv6 counterpart of 192.168.x: private, routable inside
    /// the site, never on the internet.</summary>
    UniqueLocal,
    /// <summary><c>2000::/3</c> – global unicast, routable on the internet.</summary>
    Global,
    /// <summary><c>::1</c> – this machine, never leaves the host.</summary>
    Loopback,
    /// <summary><c>ff00::/8</c> – a group of receivers, not one host. Never a source address.</summary>
    Multicast,
    /// <summary>Ranges the IETF has withdrawn: site-local <c>fec0::/10</c> (RFC 3879) and IPv4-compatible
    /// <c>::a.b.c.d</c> (RFC 4291). Still seen on old gear, but nothing should be using them.</summary>
    Deprecated,
    /// <summary><c>::</c> – "no address"; a placeholder, not something you can talk to.</summary>
    Unspecified,
    /// <summary>A valid IPv6 address in none of the ranges above.</summary>
    Other,
}

/// <summary>Classifies IPv6 addresses by scope. Pure and side-effect free, so it can be pinned by tests.</summary>
public static class Ipv6Scope
{
    /// <summary>Classifies one address. Accepts a plain address or one with a zone/scope id
    /// ("fe80::1%eth0") and a "/64" suffix, both of which turn up in device output.</summary>
    public static Ipv6Kind Classify(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return Ipv6Kind.None;

        var text = address.Trim();
        var slash = text.IndexOf('/');            // prefix length, e.g. "fd00::1/64"
        if (slash >= 0) text = text[..slash];
        var percent = text.IndexOf('%');          // zone id, e.g. "fe80::1%eth0"
        if (percent >= 0) text = text[..percent];
        text = text.Trim('[', ']');               // bracketed form from URLs

        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            return Ipv6Kind.None;

        if (ip.IsIPv6LinkLocal) return Ipv6Kind.LinkLocal;
        if (ip.IsIPv6Multicast) return Ipv6Kind.Multicast;
        if (IPAddress.IPv6Loopback.Equals(ip)) return Ipv6Kind.Loopback;
        if (IPAddress.IPv6Any.Equals(ip)) return Ipv6Kind.Unspecified;

        var b = ip.GetAddressBytes();

        // ⚠️ Unique local is fc00::/7 – the top SEVEN bits, so both 0xFC and 0xFD match. Testing for 0xFD
        // alone (the half everyone actually uses) would misfile an fc00:: address as global.
        if ((b[0] & 0xFE) == 0xFC) return Ipv6Kind.UniqueLocal;

        // Site-local fec0::/10 – withdrawn by RFC 3879, but old gear still hands it out.
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return Ipv6Kind.Deprecated;

        // Global unicast: 2000::/3.
        if ((b[0] & 0xE0) == 0x20) return Ipv6Kind.Global;

        // IPv4-compatible (::a.b.c.d) – deprecated by RFC 4291. The first 96 bits are zero and the rest is
        // not, which is what separates it from :: (unspecified, handled above) and ::1 (loopback).
        // ⚠️ IPv4-*mapped* (::ffff:a.b.c.d) is NOT this and is not deprecated – bytes 10/11 are 0xFF there.
        if (b.Take(10).All(x => x == 0) && b[10] == 0 && b[11] == 0) return Ipv6Kind.Deprecated;

        return Ipv6Kind.Other;
    }

    /// <summary>The distinct scopes present in a set of addresses, ordered most useful first (global before
    /// local) – that ordering is what makes the tag column readable at a glance.</summary>
    public static IReadOnlyList<Ipv6Kind> Summarise(IEnumerable<string> addresses)
    {
        var kinds = addresses.Select(Classify).Where(k => k != Ipv6Kind.None).Distinct().ToList();
        return kinds.OrderBy(k => k switch
        {
            Ipv6Kind.Global => 0,
            Ipv6Kind.UniqueLocal => 1,
            Ipv6Kind.LinkLocal => 2,
            Ipv6Kind.Deprecated => 3,   // worth noticing – ahead of the merely uninteresting ones
            Ipv6Kind.Multicast => 4,
            Ipv6Kind.Loopback => 5,
            Ipv6Kind.Unspecified => 6,
            _ => 7,
        }).ToList();
    }
}
