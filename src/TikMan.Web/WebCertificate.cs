using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TikMan.Core.Storage;

namespace TikMan.Web;

/// <summary>Supplies the TLS certificate for the web server. Either loads the user's own .pfx, or
/// generates a self-signed certificate and caches it under the app data folder so it stays stable
/// across restarts – a browser's "trust this certificate" exception then keeps working. The cache is
/// protected by <see cref="CredentialProtector"/> (DPAPI on Windows, AES on Linux/macOS), so the whole
/// server, TLS included, is cross-platform.</summary>
public static class WebCertificate
{
    private static string CachePath => Path.Combine(DeviceStore.StorageDirectory, "webserver.pfx");

    /// <summary>Returns a certificate with a usable private key for <c>SslStream</c> server auth.
    /// <paramref name="ownPfxPath"/> non-empty ⇒ load that (throws on a bad file/password so the caller
    /// can tell the user); empty ⇒ reuse the cached self-signed cert, or make and cache a new one.</summary>
    public static X509Certificate2 LoadOrCreate(string ownPfxPath, string ownPfxPassword)
    {
        if (ownPfxPath.Length > 0)
            // Export+reimport so the private key is reliably usable by SslStream on Windows.
            return Reimport(X509CertificateLoader.LoadPkcs12FromFile(ownPfxPath, ownPfxPassword,
                X509KeyStorageFlags.Exportable));

        var cached = TryLoadCached();
        if (cached is not null) return cached;

        var created = CreateSelfSigned();
        try
        {
            Directory.CreateDirectory(DeviceStore.StorageDirectory);
            var pfx = created.Export(X509ContentType.Pfx);
            File.WriteAllText(CachePath, CredentialProtector.ProtectBytes(pfx));
        }
        catch { /* not persisting is fine – we just regenerate next time */ }
        return created;
    }

    private static X509Certificate2? TryLoadCached()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var pfx = CredentialProtector.UnprotectBytes(File.ReadAllText(CachePath));
            if (pfx is null) return null; // cache from another user/OS – regenerate
            var cert = Reimport(X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable));
            return cert.NotAfter > DateTime.Now.AddDays(7) ? cert : null; // renew if (nearly) expired
        }
        catch { return null; }
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=TikMan", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        try
        {
            var host = Dns.GetHostName();
            san.AddDnsName(host);
            foreach (var ip in Dns.GetHostAddresses(host))
                if (!IPAddress.IsLoopback(ip)) san.AddIpAddress(ip);
        }
        catch { /* SANs are best-effort; localhost is always in */ }
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        return Reimport(cert);
    }

    /// <summary>Round-trips a cert through a PFX so the private key is bound in a way SslStream accepts.</summary>
    private static X509Certificate2 Reimport(X509Certificate2 cert)
    {
        var pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}
