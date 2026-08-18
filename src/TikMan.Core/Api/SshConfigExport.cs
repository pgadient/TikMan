using Renci.SshNet;

namespace TikMan.Core.Api;

/// <summary>Fetches a RouterOS config export (.rsc) over SSH by running <c>/export</c> – the encrypted,
/// always-available alternative to the REST export when a device's HTTPS is broken. Same non-ETM MAC
/// shim as the rest of the app (Zyxel firewalls miscompute encrypt-then-MAC). The password is used only
/// to authenticate; it is never logged.</summary>
public static class SshConfigExport
{
    public static async Task<string?> GetAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        try
        {
            // Reuses the device's held, serialized session (see SshSessionPool), exec-only like the other
            // RouterOS reads – so a config backup right after a monitoring read pays no second handshake.
            ConnectionInfo Info() =>
                SshCompat.PasswordOrInteractive(host, port, user, password, TimeSpan.FromSeconds(12)).WithCompatibleMacs();
            var session = SshSessionPool.GetOrCreate(SshSessionPool.KeyFor(host, port), () => new SshSession(Info));
            return await session.RunClientAsync(ssh =>
            {
                using var cmd = ssh.CreateCommand("/export");
                cmd.CommandTimeout = TimeSpan.FromSeconds(40);
                var output = cmd.Execute();
                return string.IsNullOrWhiteSpace(output) ? null : output;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception) { return null; } // SSH off / bad creds / not RouterOS
    }
}
