using Renci.SshNet;
using Renci.SshNet.Common;

namespace TikMan.Core.Api;

/// <summary>An interactive terminal session bridged to a transport (the web server's WebSocket).
/// Kept as an interface so the transport layer doesn't depend on SSH.NET and can be faked in tests.</summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>Raised (on a background thread) whenever the remote produces output.</summary>
    event Action<byte[]> DataReceived;

    /// <summary>Sends input (keystrokes) to the remote.</summary>
    void Write(byte[] data, int count);
}

/// <summary>A live SSH shell over SSH.NET, exposing the shell's output as an event and its input as a
/// write – so the web server can pump it to/from a browser terminal. Uses the same non-ETM MAC
/// compatibility shim as the rest of the app (Zyxel firewalls miscompute encrypt-then-MAC).</summary>
public sealed class SshTerminalSession : ITerminalSession
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;

    public event Action<byte[]>? DataReceivedRaw;
    event Action<byte[]> ITerminalSession.DataReceived
    {
        add => DataReceivedRaw += value;
        remove => DataReceivedRaw -= value;
    }

    private SshTerminalSession(SshClient client, ShellStream shell)
    {
        _client = client;
        _shell = shell;
        _shell.DataReceived += (_, e) => DataReceivedRaw?.Invoke(e.Data);
    }

    /// <summary>A connection attempt's outcome: the live session, or the reason it failed. Exactly one of
    /// the two is set. The message is what SSH.NET actually reported (algorithm mismatch, auth failure,
    /// timeout) – worth surfacing, because "couldn't connect" with no reason sends the user guessing.</summary>
    public readonly record struct ConnectResult(SshTerminalSession? Session, string Error)
    {
        public bool Ok => Session is not null;
    }

    /// <summary>Connects and opens an interactive shell. Returns null on any failure (bad credentials,
    /// unreachable host, SSH disabled). The password is used only to authenticate; it is never stored.</summary>
    public static async Task<SshTerminalSession?> ConnectAsync(string host, int port, string user,
        string password, uint cols, uint rows, CancellationToken ct = default) =>
        (await ConnectDiagnosticAsync(host, port, user, password, cols, rows, ct).ConfigureAwait(false)).Session;

    /// <summary>Like <see cref="ConnectAsync"/> but keeps the failure reason instead of collapsing it to
    /// null – so a caller can tell the user <i>why</i> (and, for an algorithm mismatch with an old device,
    /// fall back to the external OpenSSH client, which offers the legacy ssh-rsa the built-in stack may
    /// not).</summary>
    public static async Task<ConnectResult> ConnectDiagnosticAsync(string host, int port, string user,
        string password, uint cols, uint rows, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                var conn = new ConnectionInfo(host, port is > 0 and <= 65535 ? port : 22, user,
                    new PasswordAuthenticationMethod(user, password))
                {
                    Timeout = TimeSpan.FromSeconds(12),
                }.WithCompatibleMacs();
                var ssh = new SshClient(conn);
                ssh.Connect();
                var shell = ssh.CreateShellStream("xterm-256color",
                    cols is > 0 and <= 500 ? cols : 120, rows is > 0 and <= 300 ? rows : 32, 0, 0, 16384);
                return new ConnectResult(new SshTerminalSession(ssh, shell), "");
            }, ct).ConfigureAwait(false);
        }
        // ⚠️ The password is NEVER put in the message: SSH.NET's own exception text does not contain it, and
        // this string is shown in the UI and could be copied into a report.
        catch (Exception ex) { return new ConnectResult(null, ex.Message); }
    }

    public void Write(byte[] data, int count)
    {
        _shell.Write(data, 0, count);
        _shell.Flush();
    }

    public void Dispose()
    {
        try { _shell.Dispose(); } catch { }
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
