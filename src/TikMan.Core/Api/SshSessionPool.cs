using System;
using System.Collections.Concurrent;

namespace TikMan.Core.Api;

/// <summary>The set of live <see cref="SshSession"/>s, one per device, keyed by the caller. Holds the
/// persistent sessions so every SSH read to a given box reuses the same serialized connection.
///
/// <para>⚠️ Lifecycle. A session is created on first use and then held. It must be dropped when its login
/// stops being valid or wanted:
/// <list type="bullet">
/// <item><see cref="Invalidate"/> when a device's credentials change – the old session still holds the old
/// login open, so it is disposed and the next read builds a fresh one with the new credentials;</item>
/// <item><see cref="DisposeAll"/> on shutdown – releases every held device login gracefully rather than
/// leaving it to the socket dropping on process exit.</item>
/// </list></para></summary>
public static class SshSessionPool
{
    private static readonly ConcurrentDictionary<string, SshSession> _sessions = new();

    /// <summary>The pool key for a device: one persistent session per host+port, whatever the vendor. Both
    /// the creator and <see cref="Invalidate"/> must use this so they cannot drift apart.</summary>
    public static string KeyFor(string host, int port) => $"{host}:{port}";

    /// <summary>The session for <paramref name="key"/> (one per device), creating it via
    /// <paramref name="create"/> the first time. ⚠️ <paramref name="create"/> runs only when the key is
    /// absent, so it captures the credentials of the FIRST caller – a later credential change must
    /// <see cref="Invalidate"/> the key so the next call rebuilds with the new ones.</summary>
    public static SshSession GetOrCreate(string key, Func<SshSession> create) =>
        _sessions.GetOrAdd(key, _ => create());

    /// <summary>Disposes and forgets the session for <paramref name="key"/> (releasing its device login), so
    /// the next <see cref="GetOrCreate"/> opens a fresh one. Safe to call for an unknown key.</summary>
    public static void Invalidate(string key)
    {
        if (_sessions.TryRemove(key, out var s)) s.Dispose();
    }

    /// <summary>Disposes every held session – call on shutdown to release all device logins.</summary>
    public static void DisposeAll()
    {
        foreach (var key in System.Linq.Enumerable.ToList(_sessions.Keys))
            if (_sessions.TryRemove(key, out var s)) s.Dispose();
    }
}
