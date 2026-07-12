using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>Reads the TLS server certificate on port 443 and pulls device facts out of its subject.
/// Many embedded devices present a self-signed cert that names the model, serial and MAC – e.g. a
/// Cisco SPA ATA: CN="SPA122-RC, MAC: 007686EBFBD6, Serial: CCQ212301PK", O="Cisco Systems, Inc.".
/// This needs only the TLS handshake (no HTTP, no login, no MAC on the wire) and also reaches old
/// devices whose legacy TLS the normal HTTP client can't fetch a page from.</summary>
public static partial class TlsCertProbe
{
    public readonly record struct CertInfo(string Vendor, string Model, string Serial, string Mac);

    public static async Task<CertInfo?> QueryAsync(string host, int port = 443, CancellationToken ct = default)
    {
        var bare = host.Trim('[', ']');
#pragma warning disable SYSLIB0039 // some old devices only complete a handshake capped at TLS 1.0/1.1
        // Modern first; then a legacy-only retry. Ancient devices (e.g. a Cisco SPA ATA) mis-handle a
        // ClientHello that even offers TLS 1.2/1.3 and only succeed when the client caps at TLS 1.1.
        var attempts = new[] { SslProtocols.Tls13 | SslProtocols.Tls12, SslProtocols.Tls11 | SslProtocols.Tls };
#pragma warning restore SYSLIB0039
        foreach (var protocols in attempts)
        {
            if (ct.IsCancellationRequested) break;
            if (await HandshakeCertAsync(bare, port, protocols, ct).ConfigureAwait(false) is { } cert)
                return Parse(cert);
        }
        return null;
    }

    private static async Task<X509Certificate2?> HandshakeCertAsync(string host, int port,
        SslProtocols protocols, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(4));
                await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
            }

            X509Certificate2? cert = null;
            using var ssl = new SslStream(tcp.GetStream(), false,
                (_, c, _, _) => { if (c is not null) cert = new X509Certificate2(c); return true; });
            var options = new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = protocols };
            using (var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                authCts.CancelAfter(TimeSpan.FromSeconds(5));
                await ssl.AuthenticateAsClientAsync(options, authCts.Token).ConfigureAwait(false);
            }
            return cert ?? (ssl.RemoteCertificate is { } rc ? new X509Certificate2(rc) : null);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                   or OperationCanceledException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static CertInfo? Parse(X509Certificate2 cert)
    {
        var subject = cert.Subject; // E=…, CN="SPA122-RC, MAC: …, Serial: …", O="Cisco Systems, Inc.", …
        var cn = Rdn(subject, "CN");
        var org = Rdn(subject, "O");

        var macMatch = MacRegex().Match(cn);
        var mac = macMatch.Success ? FormatMac(macMatch.Groups[1].Value) : "";
        var serialMatch = SerialRegex().Match(cn);
        var serial = serialMatch.Success ? serialMatch.Groups[1].Value : "";
        // Model = the CN's leading token before a comma, if it looks like a product code (has a digit,
        // no dot/space so it isn't an IP or a hostname). Generic certs (CN=host / CN=192.168.x) yield "".
        var head = cn.Split(',')[0].Trim();
        // Some certs append the MAC to the model with a separator ("nwa5123-ac-hd_5C6A80E75F41"): use
        // it as the MAC if we don't have one, and drop it from the model.
        var glued = MacSuffixRegex().Match(head);
        if (glued.Success)
        {
            if (mac.Length == 0) mac = FormatMac(glued.Groups[1].Value);
            head = head[..glued.Index];
        }
        var model = head.Length is > 1 and <= 40 && head.Any(char.IsDigit)
                    && !head.Contains('.') && !head.Contains(' ') ? head : "";

        return model.Length == 0 && serial.Length == 0 && mac.Length == 0 && org.Length == 0
            ? null : new CertInfo(org, model, serial, mac);
    }

    /// <summary>Value of an X.500 subject RDN (KEY=value, or KEY="quoted, value" when it has commas).</summary>
    private static string Rdn(string subject, string key)
    {
        var m = Regex.Match(subject, $@"(?:^|,)\s*{key}=(""[^""]*""|[^,]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var v = m.Groups[1].Value.Trim();
        return v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1].Trim() : v;
    }

    private static string FormatMac(string hex12) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => hex12.Substring(i * 2, 2))).ToUpperInvariant();

    [GeneratedRegex(@"MAC:\s*([0-9A-Fa-f]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"Serial:\s*([A-Za-z0-9._-]{3,})", RegexOptions.IgnoreCase)]
    private static partial Regex SerialRegex();

    [GeneratedRegex(@"[_ ]([0-9A-Fa-f]{12})$")]
    private static partial Regex MacSuffixRegex();
}
