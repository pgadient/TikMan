using System.Net;
using System.Net.Sockets;

namespace TikMan.Core.Discovery;

/// <summary>Minimal SNMPv1 client that reads the standard system OIDs (sysDescr, sysName) with the
/// public community. Many devices – printers/copiers, switches, NAS, UPS, servers – expose their
/// exact model in sysDescr without any authentication. Hand-rolled BER so there is no dependency.</summary>
public static class SnmpProbe
{
    public readonly record struct SnmpInfo(string SysName, string SysDescr);

    private static readonly int[] SysDescrOid = { 1, 3, 6, 1, 2, 1, 1, 1, 0 };
    private static readonly int[] SysNameOid = { 1, 3, 6, 1, 2, 1, 1, 5, 0 };

    public static async Task<SnmpInfo?> QueryAsync(string host, CancellationToken ct = default,
        string community = "public")
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return null;
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            udp.Client.ReceiveTimeout = 1500;
            var request = BuildGetRequest(community, requestId: 0x54494B4D & 0x7FFFFFFF); // "TIKM"
            await udp.SendAsync(request, request.Length, new IPEndPoint(ip, 161)).ConfigureAwait(false);

            var receiveTask = udp.ReceiveAsync();
            if (await Task.WhenAny(receiveTask, Task.Delay(1500, ct)).ConfigureAwait(false) != receiveTask)
                return null; // no answer – SNMP disabled or wrong community
            var reply = (await receiveTask.ConfigureAwait(false)).Buffer;

            var vars = ParseResponse(reply);
            var descr = vars.FirstOrDefault(v => v.Oid.StartsWith("1.3.6.1.2.1.1.1")).Value ?? "";
            var name = vars.FirstOrDefault(v => v.Oid.StartsWith("1.3.6.1.2.1.1.5")).Value ?? "";
            return descr.Length > 0 || name.Length > 0 ? new SnmpInfo(name.Trim(), descr.Trim()) : null;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Walks an OID subtree with GetNext requests and returns every varbind in it – OID, BER
    /// tag and raw value bytes. This is what table reads (the bridge FDB, ifName) are made of. Null
    /// when the device never answered at all; a partial list when it stopped mid-walk.</summary>
    public static async Task<List<(string Oid, byte Tag, byte[] Value)>?> WalkAsync(
        string host, string community, int[] rootOid, int maxRows = 4096, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return null;
        var rows = new List<(string, byte, byte[])>();
        var rootPrefix = string.Join('.', rootOid) + ".";
        var current = rootOid;
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            for (int i = 0; i < maxRows && !ct.IsCancellationRequested; i++)
            {
                var request = BuildRequest(0xA1, community, i + 1, current); // GetNext
                await udp.SendAsync(request, request.Length, new IPEndPoint(ip, 161)).ConfigureAwait(false);
                var receiveTask = udp.ReceiveAsync();
                if (await Task.WhenAny(receiveTask, Task.Delay(1200, ct)).ConfigureAwait(false) != receiveTask)
                    return rows.Count > 0 ? rows : null;   // silence mid-walk: keep what we have

                var vb = ParseFirstVarbind((await receiveTask.ConfigureAwait(false)).Buffer);
                if (vb is null) break;                                        // error status – end of table
                var (oid, tag, value) = vb.Value;
                if (!oid.StartsWith(rootPrefix, StringComparison.Ordinal)) break; // walked past the subtree
                rows.Add((oid, tag, value));
                current = oid.Split('.').Select(int.Parse).ToArray();
            }
            return rows;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return rows.Count > 0 ? rows : null;
        }
    }

    // ---- BER encoding ----

    private static byte[] BuildGetRequest(string community, int requestId)
    {
        var varbinds = Seq(0x30, Concat(
            Varbind(SysDescrOid),
            Varbind(SysNameOid)));
        var pdu = Seq(0xA0, Concat(         // GetRequest-PDU
            Integer(requestId),
            Integer(0),                      // error-status
            Integer(0),                      // error-index
            varbinds));
        return Seq(0x30, Concat(
            Integer(0),                      // version = SNMPv1
            OctetString(community),
            pdu));
    }

    /// <summary>One-varbind request with the given PDU type (0xA0 Get, 0xA1 GetNext).</summary>
    private static byte[] BuildRequest(byte pduType, string community, int requestId, int[] oid)
    {
        var pdu = Seq(pduType, Concat(
            Integer(requestId),
            Integer(0),
            Integer(0),
            Seq(0x30, Varbind(oid))));
        return Seq(0x30, Concat(Integer(0), OctetString(community), pdu));
    }

    /// <summary>The first varbind of a response as (OID, tag, raw value bytes); null when the agent
    /// reported an error status – which is how a v1 walk says "end of table".</summary>
    private static (string Oid, byte Tag, byte[] Value)? ParseFirstVarbind(byte[] data)
    {
        try
        {
            int pos = 0;
            if (!ReadTlv(data, ref pos, out _, out _)) return null;   // outer SEQUENCE
            SkipTlv(data, ref pos);                                    // version
            SkipTlv(data, ref pos);                                    // community
            if (!ReadTlv(data, ref pos, out _, out _)) return null;    // PDU
            SkipTlv(data, ref pos);                                    // request-id
            var (_, esStart, esLen) = ReadHeader(data, pos);           // error-status
            int errorStatus = 0;
            for (int i = 0; i < esLen; i++) errorStatus = (errorStatus << 8) | data[esStart + i];
            if (errorStatus != 0) return null;
            pos = esStart + esLen;
            SkipTlv(data, ref pos);                                    // error-index
            if (!ReadTlv(data, ref pos, out _, out _)) return null;    // varbind list
            if (!ReadTlv(data, ref pos, out _, out _)) return null;    // first varbind
            if (data[pos] != 0x06) return null;
            int oidStart = pos;
            SkipTlv(data, ref pos);
            var oid = DecodeOid(data, oidStart);
            var (tag, vStart, vLen) = ReadHeader(data, pos);
            if (tag is 0x80 or 0x81 or 0x82) return null;              // noSuchObject/Instance, endOfMib
            var value = new byte[vLen];
            Array.Copy(data, vStart, value, 0, vLen);
            return (oid, tag, value);
        }
        catch (IndexOutOfRangeException) { return null; }              // truncated packet
    }

    private static byte[] Varbind(int[] oid) => Seq(0x30, Concat(Oid(oid), new byte[] { 0x05, 0x00 })); // value = NULL

    private static byte[] Seq(byte tag, byte[] content)
    {
        var len = Length(content.Length);
        var r = new byte[1 + len.Length + content.Length];
        r[0] = tag;
        Array.Copy(len, 0, r, 1, len.Length);
        Array.Copy(content, 0, r, 1 + len.Length, content.Length);
        return r;
    }

    private static byte[] Length(int n)
    {
        if (n < 0x80) return new[] { (byte)n };
        var bytes = new List<byte>();
        while (n > 0) { bytes.Insert(0, (byte)(n & 0xFF)); n >>= 8; }
        bytes.Insert(0, (byte)(0x80 | bytes.Count));
        return bytes.ToArray();
    }

    private static byte[] Integer(int value)
    {
        var bytes = new List<byte>();
        do { bytes.Insert(0, (byte)(value & 0xFF)); value >>= 8; } while (value != 0 && value != -1);
        if ((bytes[0] & 0x80) != 0 && value == 0) bytes.Insert(0, 0); // keep it positive
        return Seq(0x02, bytes.ToArray());
    }

    private static byte[] OctetString(string s) => Seq(0x04, System.Text.Encoding.ASCII.GetBytes(s));

    private static byte[] Oid(int[] oid)
    {
        var body = new List<byte> { (byte)(40 * oid[0] + oid[1]) };
        for (int i = 2; i < oid.Length; i++) body.AddRange(Base128(oid[i]));
        return Seq(0x06, body.ToArray());
    }

    private static IEnumerable<byte> Base128(int v)
    {
        var stack = new Stack<byte>();
        stack.Push((byte)(v & 0x7F));
        v >>= 7;
        while (v > 0) { stack.Push((byte)((v & 0x7F) | 0x80)); v >>= 7; }
        return stack;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    // ---- BER decoding (just enough to walk varbinds) ----

    private static List<(string Oid, string Value)> ParseResponse(byte[] data)
    {
        var result = new List<(string, string)>();
        int pos = 0;
        // outer SEQUENCE → version, community, PDU
        if (!ReadTlv(data, ref pos, out _, out var seqEnd)) return result;
        SkipTlv(data, ref pos); // version
        SkipTlv(data, ref pos); // community
        if (!ReadTlv(data, ref pos, out _, out var pduEnd)) return result; // PDU
        SkipTlv(data, ref pos); // request-id
        SkipTlv(data, ref pos); // error-status
        SkipTlv(data, ref pos); // error-index
        if (!ReadTlv(data, ref pos, out _, out var vbListEnd)) return result; // varbind list
        while (pos < vbListEnd)
        {
            if (!ReadTlv(data, ref pos, out _, out var vbEnd)) break; // varbind SEQUENCE
            // OID
            if (data[pos] != 0x06) { pos = vbEnd; continue; }
            int oidStart = pos;
            SkipTlv(data, ref pos);
            var oid = DecodeOid(data, oidStart);
            // value
            var (tag, vStart, vLen) = ReadHeader(data, pos);
            var value = tag == 0x04 ? System.Text.Encoding.UTF8.GetString(data, vStart, vLen) : "";
            result.Add((oid, value));
            pos = vbEnd;
        }
        return result;
    }

    private static bool ReadTlv(byte[] d, ref int pos, out int contentStart, out int end)
    {
        var (_, start, len) = ReadHeader(d, pos);
        contentStart = start; end = start + len; pos = start;
        return end <= d.Length;
    }

    private static void SkipTlv(byte[] d, ref int pos)
    {
        var (_, start, len) = ReadHeader(d, pos);
        pos = start + len;
    }

    private static (byte Tag, int ContentStart, int Length) ReadHeader(byte[] d, int pos)
    {
        byte tag = d[pos++];
        int len = d[pos++];
        if ((len & 0x80) != 0)
        {
            int n = len & 0x7F;
            len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | d[pos++];
        }
        return (tag, pos, len);
    }

    private static string DecodeOid(byte[] d, int pos)
    {
        var (_, start, len) = ReadHeader(d, pos);
        int end = start + len, i = start;
        var parts = new List<int>();
        int first = d[i++];
        parts.Add(first / 40);
        parts.Add(first % 40);
        int cur = 0;
        while (i < end)
        {
            cur = (cur << 7) | (d[i] & 0x7F);
            if ((d[i] & 0x80) == 0) { parts.Add(cur); cur = 0; }
            i++;
        }
        return string.Join('.', parts);
    }
}
