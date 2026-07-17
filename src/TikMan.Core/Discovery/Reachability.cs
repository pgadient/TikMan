using System.Net.Sockets;

namespace TikMan.Core.Discovery;

/// <summary>A liveness check that works the same, unprivileged, on every OS: a TCP connect attempt to a
/// port the device is known to have open.
/// <para>ICMP ping (<see cref="System.Net.NetworkInformation.Ping"/>) needs raw sockets, which on Linux
/// means root or a capability the app won't have when double-clicked – so it fails or lies there. A TCP
/// connect to an already-discovered open port needs no privilege and answers the only question the
/// dashboard's status dot asks: is this device still responding?</para></summary>
public static class Reachability
{
    /// <summary>True if a TCP connection to <paramref name="host"/>:<paramref name="port"/> completes
    /// within <paramref name="timeoutMs"/>. A refused connection (port closed but host up) and a timeout
    /// both read as "not reachable on this port" – which is why the caller probes a port the scan already
    /// found open, not an arbitrary one.</summary>
    public static async Task<bool> TcpProbeAsync(string host, int port, int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(host) || port is <= 0 or > 65535) return false;
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception) { return false; } // refused / unreachable / timed out / bad host
    }
}
