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

    /// <summary>Every <c>-o</c> option the launched OpenSSH client needs to reach the older embedded servers
    /// on this network, as one argument string.
    ///
    /// <para>Three problems, all from firmware that predates the SHA-2 SSH work:
    /// <list type="bullet">
    /// <item>the Zyxel firewalls' broken encrypt-then-MAC → offer only the plain HMACs (<c>MACs=…</c>);</item>
    /// <item>the old servers present only an <c>ssh-rsa</c> (RSA/SHA-1) <b>host key</b>, which OpenSSH 8.8
    /// no longer verifies by default → re-enable it with <c>HostKeyAlgorithms=+ssh-rsa</c> (the <c>+</c>
    /// appends, so modern host keys stay preferred);</item>
    /// <item>⚠️ the decisive one for TP-Link: the client tries <b>public-key</b> auth first (from the agent
    /// or <c>~/.ssh</c>), the appliance's minimal SSH server chokes on the attempt
    /// (<c>sign_and_send_pubkey: no mutual signature supported</c>, then it <b>closes the connection</b>)
    /// and never reaches the password prompt. Turning public-key auth <b>off</b> for the launched session
    /// sends the client straight to the password it was going to use anyway.</item>
    /// </list>
    /// <c>PubkeyAuthentication=no</c> is safe here: this is the interactive terminal to a network
    /// appliance, where password login is the norm, and the built-in (SSH.NET) terminal is password-only
    /// too – so the two behave the same. Re-enabling ssh-rsa for the pubkey <i>signature</i> was tried
    /// first and was not enough: the server still dropped the rejected key before offering a password.</para></summary>
    public static readonly string OpenSshCompatOptions =
        $"-o MACs={OpenSshMacList} -o HostKeyAlgorithms=+ssh-rsa -o PubkeyAuthentication=no";

    /// <summary>Builds a <see cref="ConnectionInfo"/> that offers BOTH the plain <c>password</c> method and
    /// <c>keyboard-interactive</c>. Some appliances accept <b>only</b> keyboard-interactive and reject the
    /// <c>password</c> method outright – notably <b>UniFi OS consoles (UDM/UDR/…)</b>, where the built-in SSH
    /// server (PAM-backed) answers the password prompt interactively; an interactive PuTTY/Tera Term login
    /// works while SSH.NET's <see cref="PasswordAuthenticationMethod"/> alone fails. Offering both is safe and
    /// additive: a server that supports the password method uses it first and never reaches the interactive
    /// fallback, and every interactive prompt is simply answered with the same password.
    /// <para>⚠️ The password is used only to build these auth methods for this one connection; it is never
    /// stored or logged (same rule as everywhere else).</para></summary>
    public static ConnectionInfo PasswordOrInteractive(string host, int port, string user, string password,
        TimeSpan? timeout = null)
    {
        var p = port is > 0 and <= 65535 ? port : 22;
        var pw = new PasswordAuthenticationMethod(user, password);
        var ki = new KeyboardInteractiveAuthenticationMethod(user);
        ki.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts) prompt.Response = password;
        };
        var info = new ConnectionInfo(host, p, user, pw, ki);
        if (timeout is { } t) info.Timeout = t;
        return info;
    }

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
