using System.Security.Cryptography;
using System.Text;

namespace TikMan.Core.Storage;

/// <summary>Encrypts passwords so devices.json never contains them in plaintext.
/// <para><b>Windows:</b> DPAPI bound to the user account – unchanged since 1.0, so every blob that is
/// already out there keeps decrypting. <b>Linux/macOS:</b> AES-256-GCM with a random key in a
/// user-only file (<c>chmod 600</c>) next to devices.json; those blobs carry the "u1:" prefix so the
/// two formats can never be confused. Honest caveat: the Unix key file is protected by file
/// permissions, not by the OS keyring – Keychain/libsecret can replace it later without a format
/// change (new prefix, old one stays readable).</para>
/// <para>Either way, a blob is bound to this user on this machine: a devices.json copied elsewhere
/// yields "" (same graceful behaviour DPAPI always had), and the app asks for the password again.</para></summary>
public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TikMan.v1");
    private const string UnixPrefix = "u1:";   // nonce(12) | tag(16) | ciphertext, base64
    private static readonly object KeyLock = new();
    private static byte[]? _unixKey;

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

        var key = UnixKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(blob, 0); tag.CopyTo(blob, nonce.Length); cipher.CopyTo(blob, nonce.Length + tag.Length);
        return UnixPrefix + Convert.ToBase64String(blob);
    }

    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        try
        {
            if (encrypted.StartsWith(UnixPrefix, StringComparison.Ordinal))
            {
                var blob = Convert.FromBase64String(encrypted[UnixPrefix.Length..]);
                if (blob.Length < 12 + 16) return "";
                var plain = new byte[blob.Length - 12 - 16];
                using var aes = new AesGcm(UnixKey(), 16);
                aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12 + 16), blob.AsSpan(12, 16), plain);
                return Encoding.UTF8.GetString(plain);
            }
            if (!OperatingSystem.IsWindows()) return ""; // a DPAPI blob on Unix – unreadable by design
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException
            or UnauthorizedAccessException)
        {
            return ""; // e.g. devices.json copied from another user/PC, or the key file is gone
        }
    }

    /// <summary>The per-user AES key, created on first use. Lives beside devices.json; the file is
    /// owner-read/write only. Deleting it orphans every "u1:" blob – exactly like a lost DPAPI
    /// profile, the stored logins silently become "enter the password again".</summary>
    private static byte[] UnixKey()
    {
        lock (KeyLock)
        {
            if (_unixKey is not null) return _unixKey;
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TikMan");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "credential.key");
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32) return _unixKey = existing;
                // wrong size = truncated/corrupt – fall through and start fresh
            }
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, key);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return _unixKey = key;
        }
    }
}
