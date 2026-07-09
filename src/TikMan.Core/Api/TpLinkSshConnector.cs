using Renci.SshNet;
using Renci.SshNet.Common;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Facts read from a device: what we show in the list.</summary>
public readonly record struct DeviceFacts(string Name, string Model, string HardwareVersion, string FirmwareVersion);

/// <summary>Queries TP-Link JetStream / Omada managed switches over SSH. These have no REST API,
/// but their CLI exposes <c>show system-info</c> in user-exec mode, which prints the firmware and
/// hardware version. The parser is locale-independent and label-driven so it survives small format
/// differences between models.</summary>
public static class TpLinkSshConnector
{
    /// <summary>Connects over SSH (Device.Port is the SSH port), runs <c>show system-info</c> and
    /// returns the parsed facts. Throws on connection/authentication failure.</summary>
    public static async Task<DeviceFacts> GetFactsAsync(Device device, string password, CancellationToken ct = default)
    {
        var port = device.Port is > 0 and <= 65535 ? device.Port : 22;
        var work = Task.Run(() =>
        {
            var info = new ConnectionInfo(device.Host, port, device.Username,
                new PasswordAuthenticationMethod(device.Username, password))
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            using var ssh = new SshClient(info);
            try
            {
                ssh.Connect();
                var output = RunSystemInfo(ssh);
                return ParseSystemInfo(output);
            }
            catch (SshAuthenticationException ex)
            {
                throw new RouterOsApiException(401, $"SSH login failed: {ex.Message}.");
            }
            catch (SshConnectionException ex)
            {
                throw new RouterOsApiException(0, $"SSH connection failed: {ex.Message}. Is SSH enabled (port {port})?");
            }
            finally
            {
                if (ssh.IsConnected) ssh.Disconnect();
            }
        }, ct);

        // Hard backstop: SSH.NET's own timeout doesn't always cover a stalled handshake, so never
        // let the UI hang on "connecting".
        try
        {
            return await work.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RouterOsApiException(0, $"SSH connection to {device.Host}:{port} timed out.");
        }
    }

    /// <summary>Runs the command via a plain exec channel; if the switch only offers an interactive
    /// shell (no exec), falls back to a shell stream.</summary>
    private static string RunSystemInfo(SshClient ssh)
    {
        try
        {
            using var cmd = ssh.RunCommand("show system-info");
            if (cmd.Result.Trim().Length > 0) return cmd.Result;
        }
        catch (SshException) { /* exec not supported – try the shell */ }

        try
        {
            using var shell = ssh.CreateShellStream("vt100", 80, 200, 800, 600, 4096);
            System.Threading.Thread.Sleep(600);
            shell.Read();                       // drain the banner/prompt
            shell.WriteLine("show system-info");
            System.Threading.Thread.Sleep(1200);
            return shell.Read();
        }
        catch (SshException) { return ""; }
    }

    /// <summary>Parses <c>show system-info</c> output. Lines look like
    /// "Firmware Version   - 3.0.12 Build 20241205 Rel.12345"; we split each line on the first
    /// dash into label/value and pick out the fields we care about.</summary>
    public static DeviceFacts ParseSystemInfo(string output)
    {
        string name = "", model = "", hardware = "", firmware = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int dash = line.IndexOf('-');
            if (dash <= 0) continue;
            var label = line[..dash].Trim().ToLowerInvariant();
            var value = line[(dash + 1)..].Trim();
            if (value.Length == 0) continue;

            if (label.Contains("firmware")) firmware = value;
            else if (label.Contains("hardware")) hardware = value;
            else if (label is "device name" or "system name" or "hostname") name = value;
            else if (label.Contains("device model")) model = value;
        }

        // The hardware line usually carries the model too, e.g. "TL-SG2008 3.0"; take the leading token.
        if (model.Length == 0 && hardware.Length > 0)
            model = hardware.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        return new DeviceFacts(name, model, hardware, firmware);
    }
}
