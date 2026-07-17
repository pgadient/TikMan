using System.Net.Sockets;
using System.Text;

namespace TikMan.Core.Discovery;

/// <summary>Reads a host's computer name, domain and OS version over SMB2 – <b>without any credentials</b>
/// and on every OS (pure protocol, no Windows API). This is the cross-platform stand-in for what WMI
/// gives on Windows: it names Windows PCs, servers and Samba/NAS boxes that a bare port scan leaves blank.
/// <para>How: the SMB2 session-setup handshake carries an NTLM negotiation. The very first server reply –
/// an NTLM <c>CHALLENGE</c> (type 2) message – is returned before any password is checked, and it embeds
/// the target's NetBIOS/DNS name, domain and OS build number (the classic <c>smb-os-discovery</c> trick).
/// We send the negotiate + a bare NTLM type-1, read the type-2, and parse it. No login is attempted.</para></summary>
public static class SmbInfoProbe
{
    /// <summary>What the SMB handshake revealed. Any field may be empty when the server didn't supply it.</summary>
    public readonly record struct SmbInfo(string ComputerName, string DnsName, string Domain, string OsVersion)
    {
        /// <summary>A friendly OS name from the version number (client/server can't be told apart from the
        /// number alone, so both are named); "" when there is no version.</summary>
        public string OsFriendly => FriendlyOs(OsVersion);
    }

    public static async Task<SmbInfo?> QueryAsync(string host, int port = 445, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            using var stream = client.GetStream();

            await WriteFramedAsync(stream, BuildNegotiate(), timeout.Token).ConfigureAwait(false);
            _ = await ReadFramedAsync(stream, timeout.Token).ConfigureAwait(false);      // negotiate response

            await WriteFramedAsync(stream, BuildSessionSetup(), timeout.Token).ConfigureAwait(false);
            var resp = await ReadFramedAsync(stream, timeout.Token).ConfigureAwait(false); // session-setup: NTLM type 2
            if (resp is null) return null;

            var ntlm = FindNtlmMessage(resp);
            return ntlm is null ? null : ParseChallenge(ntlm);
        }
        catch (Exception) { return null; } // not SMB / closed / timed out
    }

    // ---- packet construction ---------------------------------------------------------------------

    private static byte[] Smb2Header(ushort command, ulong messageId)
    {
        var h = new byte[64];
        h[0] = 0xFE; h[1] = (byte)'S'; h[2] = (byte)'M'; h[3] = (byte)'B'; // ProtocolId
        WriteU16(h, 4, 64);                 // StructureSize
        WriteU16(h, 12, command);           // Command
        WriteU64(h, 24, messageId);         // MessageId
        return h;
    }

    private static byte[] BuildNegotiate()
    {
        ushort[] dialects = { 0x0202, 0x0210, 0x0300, 0x0302 }; // up to 3.0.2 – no 3.1.1 negotiate contexts
        var body = new byte[36 + dialects.Length * 2];
        WriteU16(body, 0, 36);                       // StructureSize
        WriteU16(body, 2, (ushort)dialects.Length);  // DialectCount
        WriteU16(body, 4, 1);                        // SecurityMode = signing enabled
        // Capabilities(8..12)=0, ClientGuid(12..28)=0 (fine for a probe), NegotiateContext fields unused here.
        for (int i = 0; i < dialects.Length; i++) WriteU16(body, 36 + i * 2, dialects[i]);
        return Concat(Smb2Header(0x0000, 0), body);
    }

    private static byte[] BuildSessionSetup()
    {
        var ntlm = BuildNtlmNegotiate();
        var body = new byte[24 + ntlm.Length];
        WriteU16(body, 0, 25);               // StructureSize (fixed, buffer follows)
        body[2] = 0;                         // Flags
        body[3] = 1;                         // SecurityMode = signing enabled
        WriteU32(body, 4, 0);                // Capabilities
        WriteU32(body, 8, 0);                // Channel
        WriteU16(body, 12, (ushort)(64 + 24)); // SecurityBufferOffset (header + this 24-byte body)
        WriteU16(body, 14, (ushort)ntlm.Length); // SecurityBufferLength
        WriteU64(body, 16, 0);               // PreviousSessionId
        Array.Copy(ntlm, 0, body, 24, ntlm.Length);
        return Concat(Smb2Header(0x0001, 1), body);
    }

    private static byte[] BuildNtlmNegotiate()
    {
        var m = new byte[40];
        Encoding.ASCII.GetBytes("NTLMSSP\0").CopyTo(m, 0); // signature (8)
        WriteU32(m, 8, 1);                                 // MessageType = NEGOTIATE
        // Flags: UNICODE|OEM|REQUEST_TARGET|NTLM|ALWAYS_SIGN|NEGOTIATE_VERSION|128|56
        WriteU32(m, 12, 0x1 | 0x2 | 0x4 | 0x200 | 0x8000 | 0x02000000 | 0x20000000u | 0x80000000u);
        // DomainNameFields(16..24)=0, WorkstationFields(24..32)=0, Version(32..40)=0 (server ignores ours).
        return m;
    }

    // ---- response parsing ------------------------------------------------------------------------

    /// <summary>Finds the "NTLMSSP" message inside the session-setup security buffer, whether it is bare
    /// or wrapped in a SPNEGO/GSS token (we just scan for the signature).</summary>
    private static byte[]? FindNtlmMessage(byte[] data)
    {
        var sig = Encoding.ASCII.GetBytes("NTLMSSP\0");
        for (int i = 0; i + sig.Length <= data.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < sig.Length; j++) if (data[i + j] != sig[j]) { hit = false; break; }
            if (hit) return data[i..];
        }
        return null;
    }

    /// <summary>Parses an NTLM CHALLENGE (type 2): the OS version (from the Version field) and the target
    /// info AV-pairs (NetBIOS/DNS computer name, domain). Pure, so the smoke test can pin it.</summary>
    public static SmbInfo ParseChallenge(byte[] m)
    {
        if (m.Length < 48) return default;
        // Version field at offset 48 (present when NEGOTIATE_VERSION was set): major.minor.build.
        string os = "";
        if (m.Length >= 56)
        {
            int major = m[48], minor = m[49];
            int build = ReadU16(m, 50);
            if (major != 0) os = $"{major}.{minor}.{build}";
        }
        // TargetInfoFields at offset 40: Len(2), MaxLen(2), Offset(4).
        int tiLen = ReadU16(m, 40);
        int tiOff = (int)ReadU32(m, 44);
        string nbName = "", dnsName = "", nbDomain = "", dnsDomain = "";
        if (tiOff > 0 && tiLen > 0 && tiOff + tiLen <= m.Length)
        {
            int p = tiOff;
            while (p + 4 <= tiOff + tiLen)
            {
                int avId = ReadU16(m, p);
                int avLen = ReadU16(m, p + 2);
                p += 4;
                if (avId == 0 || p + avLen > m.Length) break; // MsvAvEOL
                var val = Encoding.Unicode.GetString(m, p, avLen);
                switch (avId)
                {
                    case 1: nbName = val; break;      // MsvAvNbComputerName
                    case 2: nbDomain = val; break;    // MsvAvNbDomainName
                    case 3: dnsName = val; break;     // MsvAvDnsComputerName
                    case 4: dnsDomain = val; break;   // MsvAvDnsDomainName
                }
                p += avLen;
            }
        }
        return new SmbInfo(nbName, dnsName, nbDomain.Length > 0 ? nbDomain : dnsDomain, os);
    }

    /// <summary>Version number → a friendly Windows name. Client and server share build numbers below the
    /// Windows-10 line, so both are named; ≥ Windows 10 it is left as the family name.</summary>
    public static string FriendlyOs(string version)
    {
        if (version.Length == 0) return "";
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min))
            return "";
        int build = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 0;
        return (maj, min) switch
        {
            (10, 0) when build >= 22000 => $"Windows 11 / Server (10.0.{build})",
            (10, 0) => $"Windows 10 / Server (10.0.{build})",
            (6, 3) => "Windows 8.1 / Server 2012 R2",
            (6, 2) => "Windows 8 / Server 2012",
            (6, 1) => "Windows 7 / Server 2008 R2",
            (6, 0) => "Windows Vista / Server 2008",
            (5, _) => "Windows XP / Server 2003",
            _ => $"Windows ({version})",
        };
    }

    // ---- framing + little-endian helpers ---------------------------------------------------------

    /// <summary>Writes a NetBIOS-framed SMB message: a 4-byte header (0x00 + 3-byte big-endian length).</summary>
    private static async Task WriteFramedAsync(NetworkStream s, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = 0;
        frame[1] = (byte)(payload.Length >> 16);
        frame[2] = (byte)(payload.Length >> 8);
        frame[3] = (byte)payload.Length;
        Array.Copy(payload, 0, frame, 4, payload.Length);
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFramedAsync(NetworkStream s, CancellationToken ct)
    {
        var head = await ReadExactlyAsync(s, 4, ct).ConfigureAwait(false);
        if (head is null) return null;
        int len = head[1] << 16 | head[2] << 8 | head[3];
        if (len is <= 0 or > 131072) return null;
        return await ReadExactlyAsync(s, len, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadExactlyAsync(NetworkStream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(got, count - got), ct).ConfigureAwait(false);
            if (n <= 0) return null;
            got += n;
        }
        return buf;
    }

    private static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WriteU32(byte[] b, int o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
    private static void WriteU64(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }
    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
    private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
    private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }
}
