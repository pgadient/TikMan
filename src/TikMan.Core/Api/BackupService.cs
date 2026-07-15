using TikMan.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TikMan.Core.Api;

/// <summary>Fetches a binary full backup (.backup) from the device – entirely over the encrypted SSH
/// service (port 22, enabled out of the box): create the backup with <c>/system backup save</c>,
/// download the file over SCP, then remove it again. No REST/HTTP involved, so it stays secure and
/// works even when the device's HTTPS is broken. The password is used only to authenticate; never logged.</summary>
public static class BackupService
{
    public const string WebNotAvailable =
        "The web/WebFig download is not available (MikroTik's encrypted jsproxy protocol). Please use SSH.";

    private const string BaseName = "mtmonitor-backup";
    private const string RemoteFile = BaseName + ".backup";

    public static async Task DownloadFullBackupAsync(
        Device device, string password, BackupMethod method, int sshPort,
        string localPath, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (method == BackupMethod.Web) throw new NotSupportedException(WebNotAvailable);

        var port = sshPort is > 0 and <= 65535 ? sshPort : 22;
        ConnectionInfo Info() => new ConnectionInfo(device.Host, port, device.Username,
            new PasswordAuthenticationMethod(device.Username, password))
        { Timeout = TimeSpan.FromSeconds(12) }.WithCompatibleMacs();

        var work = Task.Run(() =>
        {
            // 1) Create the backup on the device (over SSH).
            log?.Report("Creating backup on the device (SSH)…");
            using (var ssh = new SshClient(Info()))
            {
                ConnectSsh(ssh, device.Host, port);
                try
                {
                    using var create = ssh.CreateCommand($"/system backup save name={BaseName}");
                    create.CommandTimeout = TimeSpan.FromSeconds(40);
                    create.Execute();
                }
                finally { if (ssh.IsConnected) ssh.Disconnect(); }
            }

            Thread.Sleep(1500); // let RouterOS finish writing the file

            // 2) Download over SCP.
            log?.Report($"Downloading {RemoteFile} via SSH/SCP from {device.Host}:{port}…");
            using (var scp = new ScpClient(Info()))
            {
                ConnectSsh(scp, device.Host, port);
                try
                {
                    using var file = File.Create(localPath);
                    scp.Download(RemoteFile, file);
                }
                finally { if (scp.IsConnected) scp.Disconnect(); }
            }

            // 3) Remove the device file again (best effort).
            try
            {
                using var ssh = new SshClient(Info());
                ssh.Connect();
                using var rm = ssh.CreateCommand($"/file remove {RemoteFile}");
                rm.Execute();
                if (ssh.IsConnected) ssh.Disconnect();
            }
            catch { /* cleanup is optional */ }
        }, ct);

        // Hard backstop so a stalled SSH handshake can't hang the app.
        try { await work.WaitAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new RouterOsApiException(0, $"SSH backup of {device.Host}:{port} timed out."); }
    }

    private static void ConnectSsh(BaseClient client, string host, int port)
    {
        try { client.Connect(); }
        catch (SshAuthenticationException ex)
        {
            throw new RouterOsApiException(401, $"SSH login failed: {ex.Message}. The user needs the ssh (and ftp) policy.");
        }
        catch (SshConnectionException ex)
        {
            throw new RouterOsApiException(0, $"SSH connection failed: {ex.Message}. Is the SSH service enabled (port {port})?");
        }
    }
}
