using TikMan.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TikMan.Core.Api;

/// <summary>Fetches a binary full backup (.backup) from the device. Flow: have the backup
/// created via REST, download the file using the selected transport, clean up the device file.
///
/// Transports:
/// - SSH/SCP: via the encrypted SSH service that is enabled out of the box (port 22). Reliable.
/// - Web/WebFig: would reimplement MikroTik's proprietary, encrypted /jsproxy protocol
///   (like the browser). Not yet implemented – see <see cref="WebNotAvailable"/>.</summary>
public static class BackupService
{
    public const string WebNotAvailable =
        "The web/WebFig download is not available yet (MikroTik's encrypted jsproxy " +
        "protocol still has to be developed against a real device). Please use SSH.";

    private const string BaseName = "mtmonitor-backup";
    private const string RemoteFile = BaseName + ".backup";

    public static async Task DownloadFullBackupAsync(
        Device device, string password, BackupMethod method, int sshPort,
        string localPath, IProgress<string>? log = null, CancellationToken ct = default)
    {
        // 1) Create the backup on the device (via REST/HTTP)
        log?.Report("Creating backup on the device…");
        string fileId;
        using (var client = RouterOsClient.For(device, password))
        {
            await client.CreateBinaryBackupAsync(BaseName, ct).ConfigureAwait(false);
            fileId = await client.WaitForFileIdAsync(RemoteFile, attempts: 12, delay: TimeSpan.FromMilliseconds(700), ct)
                .ConfigureAwait(false);
            if (fileId.Length == 0)
                throw new RouterOsApiException(0, "The backup was created but did not appear in the file list.");
        }

        // 2) Download
        try
        {
            switch (method)
            {
                case BackupMethod.Ssh:
                    await DownloadViaScpAsync(device.Host, sshPort, device.Username, password, localPath, log, ct)
                        .ConfigureAwait(false);
                    break;

                case BackupMethod.Web:
                    throw new NotSupportedException(WebNotAvailable);

                case BackupMethod.Auto:
                    // Web preferred, but not yet available → automatically fall back to SSH
                    log?.Report("Web download not available – falling back to SSH/SCP…");
                    await DownloadViaScpAsync(device.Host, sshPort, device.Username, password, localPath, log, ct)
                        .ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            // 3) Remove the device file again (best effort, own client/timeout)
            try
            {
                using var cleanup = RouterOsClient.For(device, password);
                await cleanup.DeleteFileAsync(fileId, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* cleanup is optional */ }
        }
    }

    private static async Task DownloadViaScpAsync(
        string host, int port, string username, string password,
        string localPath, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"Downloading {RemoteFile} via SSH/SCP from {host}:{port}…");
        await Task.Run(() =>
        {
            var info = new ConnectionInfo(host, port, username,
                new PasswordAuthenticationMethod(username, password))
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            using var scp = new ScpClient(info);
            try
            {
                scp.Connect();
                using var file = File.Create(localPath);
                scp.Download(RemoteFile, file);
            }
            catch (SshAuthenticationException ex)
            {
                throw new RouterOsApiException(401,
                    $"SSH login failed: {ex.Message}. The user needs the ssh policy.");
            }
            catch (SshConnectionException ex)
            {
                throw new RouterOsApiException(0,
                    $"SSH connection failed: {ex.Message}. Is the SSH service enabled (port {port})?");
            }
            finally
            {
                if (scp.IsConnected) scp.Disconnect();
            }
        }, ct).ConfigureAwait(false);
    }
}
