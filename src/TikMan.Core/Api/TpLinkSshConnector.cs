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
    /// <summary>Connects over SSH (Device.Port is the SSH port), runs <c>show system-info</c> via
    /// the shared <see cref="SshExec"/> runner and returns the parsed facts. Throws on
    /// connection/authentication failure so monitoring can show a meaningful status.</summary>
    public static async Task<DeviceFacts> GetFactsAsync(Device device, string password, CancellationToken ct = default)
    {
        var port = device.Port is > 0 and <= 65535 ? device.Port : 22;
        try
        {
            var results = await SshExec.RunAsync(device.Host, port, device.Username, password,
                ["show system-info"], stopWhen: null, ct).ConfigureAwait(false);
            return ParseSystemInfo(results.Count > 0 ? results[0].Output : "");
        }
        catch (SshAuthenticationException ex)
        {
            throw new RouterOsApiException(401, $"SSH login failed: {ex.Message}.");
        }
        catch (SshConnectionException ex)
        {
            throw new RouterOsApiException(0, $"SSH connection failed: {ex.Message}. Is SSH enabled (port {port})?");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new RouterOsApiException(0, $"SSH connection failed: {ex.Message}.");
        }
        catch (TimeoutException)
        {
            throw new RouterOsApiException(0, $"SSH connection to {device.Host}:{port} timed out.");
        }
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
