using System;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TikMan.Core.Api;

/// <summary>A <b>persistent, serialized</b> SSH session to one device: the connection and its interactive
/// shell are opened once and held, and every command runs through a single queue so two never overlap.
///
/// <para>⚠️ Why hold it open. Opening a fresh connection per command means a fresh key-exchange per command,
/// and on old embedded hardware a KEX is both slow (1–2 s on a Zyxel switch, more on a TP-Link) and – on a
/// 2009 GS2200 with 64 MB RAM – a hazard: several near-simultaneous KEXs, especially next to an open HTTPS
/// web session, can exhaust the embedded SSH server and fault it ("newkeys: no keys for mode 0" → Data abort
/// → reboot). One held session pays the handshake once and, because the queue serializes, TikMan never opens
/// a second login to the same box behind its own back.</para>
///
/// <para>⚠️ It does occupy one login. On a device that allows only one session per user, TikMan's held
/// session is that one – which is the deliberate trade for never handshaking during steady state. Closing
/// TikMan releases it (the process exit drops the socket); <see cref="SshSessionPool.DisposeAll"/> does it
/// gracefully, and a credential change disposes the old session via <see cref="SshSessionPool.Invalidate"/>.</para>
///
/// <para>The session survives an idle drop or a device reboot: a keepalive keeps it from timing out, and a
/// command that finds the link gone rebuilds it once and retries. Serialization is a single-permit gate, so
/// callers simply await – the queue is the wait.</para></summary>
public sealed class SshSession : IDisposable
{
    private readonly Func<ConnectionInfo> _info;
    private readonly Func<SshClient, ShellStream>? _openShell;   // null ⇒ exec-only session (e.g. RouterOS)
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SshClient? _client;
    private ShellStream? _shell;
    private bool _disposed;

    /// <param name="info">Builds a fresh <see cref="ConnectionInfo"/> for each (re)connect – the vendor owns
    /// it (host/port/credentials, and any compatibility tweaks such as <c>WithCompatibleMacs</c>).</param>
    /// <param name="openShell">Opens a shell on a freshly connected client and readies it: the vendor picks
    /// the terminal type and size, swallows the login banner, and turns the pager off, so the first real
    /// command reads clean. Vendor-specific (terminal type, prompt and pager-off command all differ), so it
    /// is supplied rather than assumed. Only called for shell-based reads (<see cref="RunAsync{T}"/>); an
    /// exec-only user (<see cref="RunClientAsync{T}"/>) never opens a shell.</param>
    public SshSession(Func<ConnectionInfo> info, Func<SshClient, ShellStream>? openShell = null)
    {
        _info = info;
        _openShell = openShell;
    }

    /// <summary>Runs <paramref name="read"/> against the held shell, serialized behind any command already in
    /// flight. Rebuilds the session once and retries if the held link had dropped. The delegate should
    /// compute its own deadline <i>inside</i> itself (it runs after the queue wait, not before).</summary>
    public async Task<T> RunAsync<T>(Func<ShellStream, T> read, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return read(Ready()); }
                catch (Exception ex) when (IsConnectionFault(ex))
                {
                    Teardown();                 // idle timeout / device reboot / network blip – rebuild once
                    return read(Ready());
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs <paramref name="use"/> against the held <see cref="SshClient"/> itself, serialized like
    /// <see cref="RunAsync{T}"/>. For connectors that drive the SSH <b>exec</b> channel (<c>CreateCommand</c>)
    /// rather than an interactive shell – e.g. RouterOS reads – so they reuse the one held connection too.
    /// <para>The shell (if one was opened for this session) is left untouched; a fresh exec channel is
    /// independent of it.</para></summary>
    public async Task<T> RunClientAsync<T>(Func<SshClient, T> use, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return use(ReadyClient()); }
                catch (Exception ex) when (IsConnectionFault(ex))
                {
                    Teardown();
                    return use(ReadyClient());
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Returns a connected client, (re)connecting if the held session is gone. Caller holds the gate.
    private SshClient ReadyClient()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SshSession));
        if (_client is { IsConnected: true }) return _client;
        Teardown();
        var client = new SshClient(_info());
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);   // keep the device from dropping an idle session
        client.Connect();
        _client = client;
        return client;
    }

    // Returns a connected, prepared shell, opening one lazily. Caller holds the gate.
    private ShellStream Ready()
    {
        if (_openShell is null) throw new InvalidOperationException("This SSH session is exec-only (no shell configured).");
        var client = ReadyClient();     // may have reconnected, which disposes any old shell (→ _shell null)
        return _shell ??= _openShell(client);
    }

    // A dropped/rebooting link, not a refusal: an auth failure is NOT here, so wrong credentials propagate
    // straight out (retrying with the same credentials would only fail again and hold the queue longer).
    private static bool IsConnectionFault(Exception ex) =>
        ex is SshConnectionException or SshOperationTimeoutException
           or System.Net.Sockets.SocketException or System.IO.IOException or ObjectDisposedException;

    private void Teardown()
    {
        try { _shell?.Dispose(); } catch { /* already gone */ }
        try { if (_client is { IsConnected: true }) _client.Disconnect(); } catch { /* already gone */ }
        try { _client?.Dispose(); } catch { /* already gone */ }
        _shell = null;
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Don't tear down under a running command; wait it out, then release the device login.
        try { _gate.Wait(TimeSpan.FromSeconds(5)); } catch { /* disposed */ }
        try { Teardown(); }
        finally { try { _gate.Release(); } catch { } _gate.Dispose(); }
    }
}
