using System;
using System.Linq;
using Renci.SshNet;

namespace TikMan.Core.Api;

/// <summary>SSH interop helpers. Some embedded SSH servers – notably Zyxel firewalls (USG/ATP/
/// ZyWALL) – miscompute the encrypt-then-MAC HMAC variants and then drop every encrypted packet
/// with "Corrupted MAC on input / message authentication code incorrect". Removing the
/// <c>*-etm@openssh.com</c> MACs from what we offer makes negotiation settle on the plain HMACs,
/// which every device we talk to (MikroTik, the APs, TP-Link switches, the firewalls) accepts.</summary>
public static class SshCompat
{
    /// <summary>The non-ETM HMACs, in preference order, that a stock OpenSSH client should offer –
    /// passed as <c>-o MACs=…</c> so the built-in terminal avoids the same buggy ETM negotiation.</summary>
    public const string OpenSshMacList = "hmac-sha2-256,hmac-sha2-512,hmac-sha1";

    /// <summary>Drops the encrypt-then-MAC HMAC variants from a SSH.NET connection so a device with a
    /// broken ETM implementation falls back to a plain HMAC instead of failing the handshake.</summary>
    public static ConnectionInfo WithCompatibleMacs(this ConnectionInfo info)
    {
        foreach (var key in info.HmacAlgorithms.Keys
                     .Where(k => k.Contains("etm", StringComparison.OrdinalIgnoreCase)).ToList())
            info.HmacAlgorithms.Remove(key);
        return info;
    }
}
