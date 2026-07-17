using Renci.SshNet;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Reads a Zyxel switch over its SSH CLI (XGS/GS series, ZyNOS).
/// <para>SSH rather than SNMP on purpose: SNMP is a service you have to switch on, and plenty of
/// switches ship with it off — the XGS1930 this was written against says <c>no service-control snmp</c>
/// in its own config. SSH is how you already reach the box.</para>
/// <para>And rather than ZON: ZON is raw Ethernet, so it needs Npcap and the same layer-2 segment.
/// <c>show system-information</c> returns the same model and firmware string over plain IP — through a
/// VPN, from another subnet, with nothing installed. ZON keeps its place for devices with no login;
/// for one that has a login, this is strictly better.</para>
/// <para>⚠️ The CLI is read-only on this series: <c>show</c> works, configuring doesn't (that's the web
/// UI). Fine here – TikMan only reads. And no abbreviations: <c>show run</c> answers "invalid command
/// run", it has to be <c>show running-config</c>.</para></summary>
public static class ZyxelSsh
{
    /// <summary>What a Zyxel switch says about itself.</summary>
    public sealed record ZyxelInfo(string Model, string Name, string Firmware, string Serial,
        string MacAddress, string HardwareVersion, string Uptime);

    private static ConnectionInfo Info(string host, int port, string user, string password) =>
        new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
            new PasswordAuthenticationMethod(user, password)) { Timeout = TimeSpan.FromSeconds(12) }
            .WithCompatibleMacs();   // Zyxel miscomputes the encrypt-then-MAC variants

    private static async Task<string?> RunAsync(string host, int port, string user, string password,
        string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var ssh = new SshClient(Info(host, port, user, password));
                ssh.Connect();
                try
                {
                    using var cmd = ssh.CreateCommand(command);
                    cmd.CommandTimeout = timeout ?? TimeSpan.FromSeconds(30);
                    return cmd.Execute();
                }
                finally { if (ssh.IsConnected) ssh.Disconnect(); }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception) { return null; } // SSH off / bad creds / not a Zyxel
    }

    /// <summary>Model, firmware, serial, MAC and uptime. Null when SSH didn't answer.</summary>
    public static async Task<ZyxelInfo?> GetInfoAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "show system-information", ct);
        return text is null ? null : ParseSystemInformation(text);
    }

    /// <summary>The running configuration – this is the config backup. Null when SSH didn't answer.
    /// <para>⚠️ Unlike RouterOS's <c>/export</c>, which hides secrets, this carries
    /// <c>admin-password cipher …</c>. The text is a credential-bearing artefact: never log it.</para></summary>
    public static async Task<string?> GetRunningConfigAsync(string host, int port, string user, string password,
        CancellationToken ct = default)
    {
        var text = await RunAsync(host, port, user, password, "show running-config", ct,
            TimeSpan.FromSeconds(60));   // a 52-port switch prints a lot
        return text is { Length: > 0 } && text.Contains("Current configuration", StringComparison.OrdinalIgnoreCase)
            ? text : null;
    }

    // ---- parsers (pure, pinned to real output) ----

    /// <summary>Parses <c>show system-information</c> (shape verbatim from an XGS1930-52HP on ZyNOS V5.00, serial and MAC anonymised):
    /// <code>
    /// Product Model           : XGS1930-52HP
    /// System Name             : XGS1930
    /// System up Time          :   194:30:49 (29bcf6c1 ticks)
    /// Ethernet Address        : bc:99:11:00:11:22
    /// ZyNOS F/W Version       : V5.00(ABHV.0) | 06/27/2025
    /// Hardware Version        : V1.4
    /// Serial Number           : S231L40001234
    /// </code>
    /// The keys are padded out to a column, and the MAC's own colons sit in the value – so the split is
    /// on the <b>first</b> colon only, which is what <see cref="ParseColonBlock"/> does.</summary>
    public static ZyxelInfo ParseSystemInformation(string text)
    {
        var d = ParseColonBlock(text);
        string Get(string key) => d.TryGetValue(key, out var v) ? v : "";
        // "194:30:49 (29bcf6c1 ticks)" – the ticks are the same number in hex and add nothing.
        var uptime = Get("System up Time");
        if (uptime.IndexOf('(') is > 0 and var paren) uptime = uptime[..paren].Trim();
        return new ZyxelInfo(
            Model: Get("Product Model"),
            Name: Get("System Name"),
            Firmware: Get("ZyNOS F/W Version"),
            Serial: Get("Serial Number"),
            MacAddress: Get("Ethernet Address"),
            HardwareVersion: Get("Hardware Version"),
            Uptime: uptime);
    }

    /// <summary>Model and firmware straight off the <c>show running-config</c> header:
    /// <code>
    /// ; Product Name = XGS1930-52HP
    /// ; Firmware Version = V5.00(ABHV.0) | 06/27/2025
    /// </code>
    /// A second source for the same two facts, free with a backup – so a device that has just been
    /// backed up needn't be asked again.</summary>
    public static (string Model, string Firmware) ParseConfigHeader(string config)
    {
        string model = "", firmware = "";
        foreach (var raw in config.Split('\n').Take(20))   // the header is the first few lines
        {
            var line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith(';')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[1..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key.Equals("Product Name", StringComparison.OrdinalIgnoreCase)) model = val;
            else if (key.Equals("Firmware Version", StringComparison.OrdinalIgnoreCase)) firmware = val;
        }
        return (model, firmware);
    }

    /// <summary>"Key : value", split on the first colon. Not RouterOsSsh.ParseColon: that one trims the
    /// key of a right-aligned block, and here the value can itself contain colons (the MAC).</summary>
    public static Dictionary<string, string> ParseColonBlock(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            var key = line[..c].Trim();
            if (key.Length > 0 && !d.ContainsKey(key)) d[key] = line[(c + 1)..].Trim();
        }
        return d;
    }
}
