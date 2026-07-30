using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

/// <summary>Hands a device off to an external program: RDP, SFTP, telnet, an FTP browser, a media player
/// for RTSP, or an SSH client. The WPF client hard-codes the Windows tools (<c>mstsc.exe</c>, <c>wt.exe</c>,
/// <c>explorer.exe</c>); this one picks per platform, because the same button has to work on Linux and macOS.
/// <para>Every launcher returns a message for the status bar instead of failing silently – "nothing happened"
/// is the worst possible answer when the user expects a window to appear.</para>
/// <para>⚠️ Passwords are never put on a command line. Command lines are readable by other processes on the
/// machine (<c>ps</c> shows them to every user on Linux by default), so the external client prompts for the
/// password itself. Only the username is passed through.</para></summary>
public static class Launchers
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>Opens a remote desktop session. Windows has mstsc; elsewhere the common free clients are
    /// tried in turn, and failing that the desktop's own rdp:// handler gets it.</summary>
    public static string Rdp(string host, int port)
    {
        var endpoint = port is > 0 and not 3389 ? $"{host}:{port}" : host;
        if (IsWindows) return Try("mstsc.exe", $"/v:{endpoint}") ? "" : T("Rdp_Failed");

        // xfreerdp/remmina are the usual Linux clients; macOS has Microsoft Remote Desktop on rdp://.
        if (Try("xfreerdp", $"/v:{endpoint}")) return "";
        if (Try("remmina", $"-c rdp://{endpoint}")) return "";
        return OpenUrl($"rdp://{endpoint}") ? "" : T("Rdp_Failed");
    }

    /// <summary>Opens an SFTP session. Uses the configured WinSCP when there is one (Windows), otherwise the
    /// desktop's sftp:// handler – GNOME Files and Finder both mount it.
    /// <para><paramref name="password"/> is only ever non-empty when the user opted in
    /// (<see cref="TikMan.Core.Storage.AppData.PassPasswordToExternalClients"/>); see the note on this
    /// class for why that is off by default.</para></summary>
    public static string Sftp(string host, int port, string user, string winScpPath, string password = "")
    {
        var auth = "";
        if (user.Length > 0)
        {
            auth = Uri.EscapeDataString(user);
            // Opt-in only: this ends up on the child's command line, where other local processes can read it.
            if (password.Length > 0) auth += ":" + Uri.EscapeDataString(password);
            auth += "@";
        }
        var session = $"sftp://{auth}{host}{(port is > 0 and not 22 ? $":{port}" : "")}/";

        var winscp = winScpPath.Trim();
        if (winscp.Length > 0 && File.Exists(winscp))
            return Try(winscp, $"\"{session}\"") ? "" : T("Av_LaunchFailed", "WinSCP");

        if (IsWindows) return T("Wsc_NoPath"); // no built-in Windows sftp browser worth opening
        return OpenUrl(session) ? "" : T("Av_LaunchFailed", "SFTP");
    }

    /// <summary>Browses an FTP host in the platform's file manager.</summary>
    public static string Ftp(string url)
    {
        // File Explorer still browses ftp:// after browsers dropped it, so go straight to it on Windows.
        if (IsWindows) return Try("explorer.exe", url) ? "" : T("Av_LaunchFailed", "FTP");
        return OpenUrl(url) ? "" : T("Av_LaunchFailed", "FTP");
    }

    /// <summary>Opens an RTSP stream: the configured VLC if there is one, else whatever owns rtsp://.</summary>
    public static string Rtsp(string url, string vlcPath)
    {
        var vlc = vlcPath.Trim();
        if (vlc.Length == 0) vlc = FindVlc() ?? "";     // nothing configured – use it if it is simply there
        if (vlc.Length > 0 && File.Exists(vlc)) return Try(vlc, url) ? "" : T("Rtsp_NoPlayer");
        if (!IsWindows && Try("vlc", url)) return "";   // usually on PATH when installed from a package
        return OpenUrl(url) ? "" : T("Rtsp_NoPlayer");
    }

    /// <summary>An installed SFTP client this platform can use, or null when none is in a standard place.
    ///
    /// <para>⚠️ Not WinSCP-only, despite the setting's name. <see cref="Sftp"/> launches the configured
    /// program with a single <c>sftp://user@host/</c> argument, and WinSCP, FileZilla and Cyberduck all
    /// accept exactly that – so the same field works on every platform, and prefilling it is worth doing
    /// everywhere rather than only on Windows. Ordered by how well each one fits that invocation.</para>
    ///
    /// <para>Returns null rather than a guess, for the same reason as <see cref="FindVlc"/>: a path that
    /// does not exist looks like a working configuration and fails later, when the user clicks SFTP.</para></summary>
    public static string? FindSftpClient()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = IsWindows
            ? new[]
            {
                Path.Combine(pf, "WinSCP", "WinSCP.exe"),
                Path.Combine(pf86, "WinSCP", "WinSCP.exe"),
                // WinSCP's installer offers a per-user install, which lands here and is easy to miss.
                Path.Combine(local, "Programs", "WinSCP", "WinSCP.exe"),
                Path.Combine(pf, "FileZilla FTP Client", "filezilla.exe"),
                Path.Combine(pf86, "FileZilla FTP Client", "filezilla.exe"),
            }
            : IsMac
                ? new[]
                {
                    "/Applications/Cyberduck.app/Contents/MacOS/Cyberduck",
                    "/Applications/FileZilla.app/Contents/MacOS/filezilla",
                }
                // Distribution packages, Snap and Flatpak each put it somewhere else.
                : new[]
                {
                    "/usr/bin/filezilla", "/usr/local/bin/filezilla", "/snap/bin/filezilla",
                    "/var/lib/flatpak/exports/bin/org.filezillaproject.Filezilla",
                };

        foreach (var c in candidates)
        {
            try { if (c.Length > 0 && File.Exists(c)) return c; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>The standard VLC location for this platform, when it is actually installed.
    ///
    /// <para>Used to prefill the setting, so the common case ("VLC is in the usual place") needs no
    /// configuration at all. ⚠️ Returns null rather than a guess: an invented path in the settings box would
    /// look like a working configuration and fail at the moment the user clicks an RTSP badge.</para></summary>
    public static string? FindVlc()
    {
        var candidates = IsWindows
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe"),
            }
            : IsMac
                ? new[] { "/Applications/VLC.app/Contents/MacOS/VLC" }
                // Distribution packages, Flatpak and Snap each put it somewhere else.
                : new[]
                {
                    "/usr/bin/vlc", "/usr/local/bin/vlc", "/snap/bin/vlc",
                    "/var/lib/flatpak/exports/bin/org.videolan.VLC",
                };

        foreach (var c in candidates)
        {
            try { if (c.Length > 0 && File.Exists(c)) return c; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>The platform's own OpenSSH client, when it is actually there.
    ///
    /// <para>Used to prefill the client path in the settings, so the box <b>shows</b> what would otherwise be
    /// launched implicitly instead of sitting empty – and anyone who wants something else (PuTTY, KiTTY) can
    /// see what they are replacing. ⚠️ Returns null rather than a guess, for the same reason as
    /// <see cref="FindVlc"/>: an invented path reads as a working configuration and only fails later, at the
    /// moment the user clicks an SSH badge.</para></summary>
    public static string? FindSystemSsh()
    {
        var candidates = IsWindows
            ? new[]
            {
                // Windows' own OpenSSH. System32 is the real one; the Sysnative view matters when a 32-bit
                // process would otherwise be redirected to SysWOW64, where ssh.exe does not exist.
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "OpenSSH", "ssh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH", "ssh.exe"),
            }
            : new[] { "/usr/bin/ssh", "/usr/local/bin/ssh", "/bin/ssh", "/opt/homebrew/bin/ssh" };

        foreach (var c in candidates)
        {
            try { if (c.Length > 0 && File.Exists(c)) return c; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Opens telnet in a terminal window. Telnet is an optional (off-by-default) Windows feature and
    /// absent from many Linux images, so a missing client is reported rather than failing quietly.</summary>
    public static string Telnet(string host, int port)
    {
        var arg = port is > 0 and not 23 ? $"{host} {port}" : host;
        return InTerminal("telnet", arg) ? "" : T("Telnet_NotInstalled");
    }

    /// <summary>Opens an interactive SSH session in an external client: the one configured in settings when
    /// enabled (PuTTY takes <c>-P</c> for the port, everything else OpenSSH-style <c>-p</c>), otherwise the
    /// platform's own terminal running the system ssh.</summary>
    public static string Ssh(string host, int port, string user, bool useExternal, string externalPath)
    {
        var target = user.Length > 0 ? $"{user}@{host}" : host;
        var external = externalPath.Trim();

        if (useExternal && external.Length > 0)
        {
            if (!File.Exists(external)) return T("Av_LaunchFailed", Path.GetFileName(external));
            var name = Path.GetFileNameWithoutExtension(external);
            bool putty = name.Contains("putty", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("kitty", StringComparison.OrdinalIgnoreCase);
            var args = putty
                ? (port != 22 ? $"-ssh {target} -P {port}" : $"-ssh {target}")
                : (port != 22 ? $"-p {port} {target}" : target);
            return Try(external, args) ? "" : T("Ssh_LaunchFailed");
        }

        // Same compatibility options as the WPF client: plain HMACs for the Zyxel firewalls' broken ETM,
        // and ssh-rsa re-enabled for TP-Link's SHA-1-only signatures (without it OpenSSH 8.8+ fails with
        // "no mutual signature supported" and the device drops the connection before the password prompt).
        // OpenSSH-only options, and this branch always launches the system ssh, so they apply everywhere.
        var opts = TikMan.Core.Api.SshCompat.OpenSshCompatOptions;
        var sshArgs = port != 22 ? $"{opts} -p {port} {target}" : $"{opts} {target}";
        return InTerminal("ssh", sshArgs) ? "" : T("Ssh_LaunchFailed");
    }

    // ---- platform plumbing ------------------------------------------------------------------------

    /// <summary>Runs a command line inside a terminal window, using whatever terminal the platform has.
    /// Windows prefers Windows Terminal and falls back to the classic console; Linux walks the usual
    /// emulators (the Debian/Ubuntu <c>x-terminal-emulator</c> alternative first); macOS scripts Terminal.app,
    /// which needs the command as one string rather than an argv.</summary>
    private static bool InTerminal(string command, string args)
    {
        if (IsWindows)
            return Try("wt.exe", $"{command} {args}") || Try("cmd.exe", $"/k {command} {args}");

        if (IsMac)
        {
            // osascript is the only reliable way to get a *new* Terminal window running a command.
            var script = $"tell application \"Terminal\" to do script \"{command} {args}\"";
            return Try("osascript", $"-e '{script}'") ||
                   Try("open", $"-a Terminal");
        }

        // Terminal argument styles differ: -e takes the rest as the command for most, konsole wants it last.
        var terminals = new (string Exe, string Args)[]
        {
            ("x-terminal-emulator", $"-e {command} {args}"),
            ("gnome-terminal",      $"-- {command} {args}"),
            ("konsole",             $"-e {command} {args}"),
            ("xfce4-terminal",      $"-e \"{command} {args}\""),
            ("xterm",               $"-e {command} {args}"),
        };
        foreach (var (exe, a) in terminals)
            if (Try(exe, a)) return true;
        return false;
    }

    /// <summary>Starts a process, reporting whether it actually launched. A missing executable throws
    /// Win32Exception rather than returning a code, which is exactly the "not installed" case.</summary>
    private static bool Try(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return p is not null;
        }
        catch { return false; }
    }

    /// <summary>Hands a URL to the desktop's registered handler.</summary>
    /// <summary>Opens a page in the default browser. Returns a status message, "" on success.</summary>
    public static string OpenWeb(string url) => OpenUrl(url) ? "" : T("Av_LaunchFailed", "Browser");

    private static bool OpenUrl(string url)
    {
        try
        {
            if (IsWindows) return Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }) is not null;
            return Process.Start(IsMac ? "open" : "xdg-open", url) is not null;
        }
        catch { return false; }
    }
}
