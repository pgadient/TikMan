using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TikMan.Core.Storage;

/// <summary>Encrypts passwords with Windows DPAPI (bound to the user account).
/// As a result, devices.json never contains plaintext passwords.</summary>
[SupportedOSPlatform("windows")]
public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TikMan.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // e.g. devices.json copied from another user/PC
        }
    }
}
