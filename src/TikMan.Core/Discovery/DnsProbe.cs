using System.Net;
using System.Net.Sockets;

namespace TikMan.Core.Discovery;

/// <summary>Minimal UDP DNS probe: sends one standard query to port 53 and reports whether the host
/// answers like a DNS server / forwarder. Home routers (e.g. a Swisscom Internet-Box) often run a
/// DNS forwarder that listens only on UDP 53, which a TCP connect scan can't see – this catches it.
/// Hand-rolled so there is no dependency.</summary>
public static class DnsProbe
{
    public static async Task<bool> IsOpenAsync(string host, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            var query = BuildQuery();
            await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 53)).ConfigureAwait(false);

            var receiveTask = udp.ReceiveAsync();
            if (await Task.WhenAny(receiveTask, Task.Delay(1200, ct)).ConfigureAwait(false) != receiveTask)
                return false; // no answer within the timeout
            var reply = (await receiveTask.ConfigureAwait(false)).Buffer;

            // A DNS reply: at least a header, our transaction ID echoed, and the QR (response) bit set.
            // The response code doesn't matter – even REFUSED/SERVFAIL means "a DNS server is here".
            return reply.Length >= 12 && reply[0] == 0x13 && reply[1] == 0x37 && (reply[2] & 0x80) != 0;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    // Standard query, recursion desired, for the root zone's NS records – something any DNS server
    // or forwarder answers (even if only with a referral or an error).
    private static byte[] BuildQuery() => new byte[]
    {
        0x13, 0x37,       // transaction ID
        0x01, 0x00,       // flags: standard query (QR=0), recursion desired
        0x00, 0x01,       // QDCOUNT = 1
        0x00, 0x00,       // ANCOUNT
        0x00, 0x00,       // NSCOUNT
        0x00, 0x00,       // ARCOUNT
        0x00,             // QNAME = root (.)
        0x00, 0x02,       // QTYPE = NS
        0x00, 0x01,       // QCLASS = IN
    };
}
