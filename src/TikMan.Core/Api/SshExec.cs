using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TikMan.Core.Api;

/// <summary>Shared SSH runner: one connection, a sequence of (read-only) commands, an exec channel
/// with interactive-shell fallback ("--More--" pagination fed with spaces). Used by the TP-Link
/// connector and the generic SSH info probe. Throws SSH.NET exceptions / TimeoutException – the
/// callers decide whether that is an error (monitoring) or a shrug (best-effort probing).</summary>
public static class SshExec
{
    public static async Task<List<(string Command, string Output)>> RunAsync(
        string host, int port, string user, string password, IReadOnlyList<string> commands,
        Func<string, bool>? stopWhen = null, CancellationToken ct = default)
    {
        var work = Task.Run(() =>
        {
            var results = new List<(string, string)>();
            var conn = new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
                new PasswordAuthenticationMethod(user, password))
            {
                Timeout = TimeSpan.FromSeconds(6),
            }.WithCompatibleMacs();
            using var ssh = new SshClient(conn);
            ssh.Connect();
            ShellStream? shell = null;
            bool execWorks = true;
            try
            {
                foreach (var command in commands)
                {
                    var output = RunOne(ssh, command, ref execWorks, ref shell);
                    results.Add((command, output));
                    if (stopWhen?.Invoke(output) == true) break;
                }
                return results;
            }
            finally
            {
                shell?.Dispose();
                if (ssh.IsConnected) ssh.Disconnect();
            }
        }, ct);

        // Hard backstop: SSH.NET's own timeout doesn't always cover a stalled handshake.
        return await work.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    /// <summary>Runs one command: over an exec channel while the device supports it, otherwise over
    /// a (shared) interactive shell, feeding space for "--More--" pagination.</summary>
    private static string RunOne(SshClient ssh, string command, ref bool execWorks, ref ShellStream? shell)
    {
        if (execWorks)
        {
            try
            {
                using var cmd = ssh.RunCommand(command);
                if (cmd.Result.Trim().Length > 0) return cmd.Result;
            }
            catch (SshException) { execWorks = false; }
        }

        try
        {
            if (shell is null)
            {
                shell = ssh.CreateShellStream("vt100", 200, 60, 800, 600, 8192);
                Thread.Sleep(700);
                shell.Read(); // drain banner/prompt
            }
            shell.WriteLine(command);
            var sb = new StringBuilder();
            int idle = 0;
            for (int i = 0; i < 12 && idle < 3; i++)
            {
                Thread.Sleep(350);
                var chunk = shell.Read();
                if (chunk.Length == 0) { if (sb.Length > 0) idle++; continue; }
                idle = 0;
                sb.Append(chunk);
                if (chunk.Contains("More", StringComparison.OrdinalIgnoreCase) && chunk.Contains('-'))
                    shell.Write(" "); // "--More--" style pagination
            }
            return sb.ToString();
        }
        catch (SshException) { return ""; }
    }
}
