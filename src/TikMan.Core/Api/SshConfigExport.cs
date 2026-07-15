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
            return await Task.Run(() =>
            {
                var info = new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
                    new PasswordAuthenticationMethod(user, password))
                {
                    Timeout = TimeSpan.FromSeconds(12),
                }.WithCompatibleMacs();
                using var ssh = new SshClient(info);
                ssh.Connect();
                try
                {
                    using var cmd = ssh.CreateCommand("/export");
                    cmd.CommandTimeout = TimeSpan.FromSeconds(40);
                    var output = cmd.Execute();
                    return string.IsNullOrWhiteSpace(output) ? null : output;
                }
                finally { if (ssh.IsConnected) ssh.Disconnect(); }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception) { return null; } // SSH off / bad creds / not RouterOS
    }
}
