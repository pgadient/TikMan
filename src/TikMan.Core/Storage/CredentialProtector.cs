using System.Security.Cryptography;
using System.Text;

namespace TikMan.Core.Storage;

/// <summary>Encrypts passwords so settings.json never contains them in plaintext.
/// <para><b>Windows:</b> DPAPI bound to the user account – unchanged since 1.0, so every blob that is
/// already out there keeps decrypting. <b>Linux/macOS:</b> AES-256-GCM with a random key in a
/// user-only file (<c>chmod 600</c>) next to settings.json; those blobs carry the "u1:" prefix so the
/// two formats can never be confused. Honest caveat: the Unix key file is protected by file
/// permissions, not by the OS keyring – Keychain/libsecret can replace it later without a format
/// change (new prefix, old one stays readable).</para>
/// <para>Either way, a blob is bound to this user on this machine: a settings.json copied elsewhere
/// yields "" (same graceful behaviour DPAPI always had), and the app asks for the password again.</para></summary>
public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TikMan.v1");
    private const string UnixPrefix = "u1:";   // nonce(12) | tag(16) | ciphertext, base64
    private static readonly object KeyLock = new();
    private static byte[]? _unixKey;

    public static string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? "" : ProtectBytes(Encoding.UTF8.GetBytes(plaintext));

    public static string Unprotect(string encrypted)
    {
        var bytes = UnprotectBytes(encrypted);
        return bytes is null ? "" : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Protects arbitrary bytes the same way as a password string – so the web-server's cached
    /// TLS certificate (a .pfx, not text) rides the identical DPAPI-on-Windows / AES-on-Unix path.
    /// Returns a base64 string (with the "u1:" prefix on Unix).</summary>
    public static string ProtectBytes(byte[] plain)
    {
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));

        var key = UnixKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(blob, 0); tag.CopyTo(blob, nonce.Length); cipher.CopyTo(blob, nonce.Length + tag.Length);
        return UnixPrefix + Convert.ToBase64String(blob);
    }

    /// <summary>Reverse of <see cref="ProtectBytes"/>; null when the blob can't be read on this
    /// machine/user (foreign profile, wrong OS format, tampered GCM tag, missing key file).</summary>
    public static byte[]? UnprotectBytes(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try
        {
            if (encrypted.StartsWith(UnixPrefix, StringComparison.Ordinal))
            {
                var blob = Convert.FromBase64String(encrypted[UnixPrefix.Length..]);
                if (blob.Length < 12 + 16) return null;
                var plain = new byte[blob.Length - 12 - 16];
                using var aes = new AesGcm(UnixKey(), 16);
                aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12 + 16), blob.AsSpan(12, 16), plain);
                return plain;
            }
            if (!OperatingSystem.IsWindows()) return null; // a DPAPI blob on Unix – unreadable by design
            return ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException
            or UnauthorizedAccessException)
        {
            return null; // e.g. settings.json copied from another user/PC, or the key file is gone
        }
    }

    /// <summary>The per-user AES key, created on first use. Lives beside settings.json; the file is
    /// owner-read/write only. Deleting it orphans every "u1:" blob – exactly like a lost DPAPI
    /// profile, the stored logins silently become "enter the password again".</summary>
    private static byte[] UnixKey()
    {
        lock (KeyLock)
        {
            if (_unixKey is not null) return _unixKey;
            // ⚠️ DeviceStore.StorageDirectory, never Environment.GetFolderPath directly: on single-file
            // Linux/macOS builds that call returns "", which turns this into the *relative* path
            // "TikMan/credential.key" – a different folder for every working directory the app is started
            // from. The settings file resolves correctly through the same helper, so the symptom was not
            // "TikMan lost its config" but the far more confusing "TikMan forgot every password": a fresh
            // key gets generated and every stored blob fails to decrypt.
            var folder = DeviceStore.StorageDirectory;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "credential.key");
            AdoptStrayKey(path);
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32)
                {
                    // Re-assert the permissions every time, not just on creation: a key restored from a
                    // backup, copied between machines or written by an older build can be world-readable,
                    // and this file protects every stored password.
                    HardenKeyFile(path);
                    return _unixKey = existing;
                }
                // Wrong size = truncated or corrupt. Keep it instead of overwriting: it is the only thing
                // that could ever decrypt the existing blobs, and destroying it turns "maybe recoverable"
                // into "certainly gone".
                TryMoveAside(path);
            }
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, key);
            HardenKeyFile(path);
            return _unixKey = key;
        }
    }

    /// <summary>Drops the cached key after the key file has been deleted, so nothing gets encrypted with a
    /// key that no longer exists on disk (those blobs would be unreadable on the next start).</summary>
    public static void ForgetCachedKey()
    {
        lock (KeyLock) _unixKey = null;
    }

    /// <summary>Rescues a key an earlier build wrote to the relative "TikMan/credential.key" (the empty
    /// <c>GetFolderPath</c> bug). Only adopted when the real location has none – it can never overwrite a
    /// good key, and it means the fix for that bug does not itself cost anyone their stored passwords.</summary>
    private static void AdoptStrayKey(string path)
    {
        try
        {
            if (File.Exists(path)) return;
            var stray = Path.Combine("TikMan", "credential.key");   // relative to the working directory
            if (!File.Exists(stray) || new FileInfo(stray).Length != 32) return;
            if (Path.GetFullPath(stray) == Path.GetFullPath(path)) return;
            File.Move(stray, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to rescue, or not allowed to – fall through and generate a fresh key.
        }
    }

    private static void HardenKeyFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryMoveAside(string path)
    {
        try
        {
            var bad = path + ".bad";
            if (!File.Exists(bad)) File.Move(path, bad); else File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
