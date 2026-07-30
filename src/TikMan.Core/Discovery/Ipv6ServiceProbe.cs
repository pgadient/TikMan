using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TikMan.Core.Discovery;

/// <summary>Checks which services a device answers on a <b>specific</b> IPv6 address.
///
/// <para>A device's addresses are not interchangeable. The firewall may accept a connection on the global
/// address and drop it on the ULA; a service may be bound to one address only; a privacy address may
/// disappear between two scans. So "the device runs SSH" (learned over IPv4) does not tell you that
/// <i>this</i> address answers on 22 – which is the question the IPv6 view exists to answer.</para>
///
/// <para>⚠️ The <b>full</b> service list is tried, not just the ports the device showed over IPv4. A service
/// can be bound to a single address, so something listening only on a v6 address is invisible to the IPv4
/// scan – deriving the port list from that result would guarantee never finding the very case this exists
/// for.</para>
///
/// <para>Affordable because of the shape: all ports of one address are attempted at once behind a short
/// timeout, so an address costs roughly one timeout in total rather than one per port. Measured: 31 ports
/// against a silent address, ~900 ms.</para></summary>
public static class Ipv6ServiceProbe
{
    /// <summary>Short on purpose. A device that is there and listening answers a local TCP handshake in
    /// milliseconds; anything slower is a firewall dropping the packet, and waiting longer only makes the
    /// scan slower without changing the answer.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(900);

    /// <summary>True when the address can be dialled – given a zone for the link-local case.
    ///
    /// <para>⚠️ Link-local (fe80::/10) cannot be dialled by address alone: the same fe80:: address can exist
    /// on every interface, so the socket needs the zone index of the one it lives on (<c>fe80::1%13</c>).
    /// With a zone it is perfectly probeable – and on a typical LAN the neighbour cache yields almost
    /// nothing else, so refusing it outright would mean never testing anything.</para></summary>
    public static bool IsProbeable(string address, long scopeId = 0)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (ip.IsIPv6Multicast) return false;
        // A link-local needs a zone: either carried in the address itself (fe80::1%13) or supplied.
        if (ip.IsIPv6LinkLocal) return ip.ScopeId != 0 || scopeId > 0;
        return true;
    }

    /// <summary>The address as a <b>string</b> that can be handed to any host-taking API (TcpClient,
    /// HttpClient, the SMB probes): a link-local without a zone gets one appended (<c>fe80::1%13</c>).
    /// Everything else is returned unchanged.
    ///
    /// <para>⚠️ Needed because the other probes take a host string, not an <see cref="IPAddress"/>. Handing
    /// them a bare <c>fe80::…</c> is not a smaller version of the same request – the socket has no link to
    /// send it on, so it fails outright.</para></summary>
    public static string WithZone(string address, long scopeId)
    {
        if (scopeId <= 0 || address.Contains('%')) return address;
        if (!IPAddress.TryParse(address, out var ip)) return address;
        return ip.IsIPv6LinkLocal ? address + "%" + scopeId : address;
    }

    /// <summary>The address as it must be handed to a socket: link-locals get the zone attached.</summary>
    private static IPAddress? Dialable(string address, long scopeId)
    {
        if (!IPAddress.TryParse(address, out var ip)) return null;
        if (ip.IsIPv6LinkLocal && ip.ScopeId == 0 && scopeId > 0) ip.ScopeId = scopeId;
        return ip;
    }

    /// <summary>Returns the subset of <paramref name="ports"/> that accept a connection on this address.</summary>
    /// <param name="scopeId">Interface index to use for a link-local address (ignored for others).</param>
    public static async Task<List<int>> ProbeAsync(string address, IReadOnlyList<int> ports,
        TimeSpan? timeout = null, long scopeId = 0, CancellationToken ct = default)
    {
        var open = new List<int>();
        if (ports.Count == 0 || !IsProbeable(address, scopeId)) return open;
        if (Dialable(address, scopeId) is not { } ip) return open;

        var budget = timeout ?? DefaultTimeout;
        var results = await Task.WhenAll(ports.Distinct()
            .Select(async p => (Port: p, Open: await IsOpenAsync(ip, p, budget, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);

        open.AddRange(results.Where(r => r.Open).Select(r => r.Port));
        open.Sort();
        return open;
    }

    private static async Task<bool> IsOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception)
        {
            // Refused, unreachable, timed out, no route – all mean the same thing here: not answering.
            return false;
        }
    }
}
