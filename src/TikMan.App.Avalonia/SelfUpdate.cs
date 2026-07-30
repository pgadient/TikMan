using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

/// <summary>Replaces this build with a newer one from the GitHub release.
/// <para>Two layouts can be swapped in place, because each is a <b>single file</b>:</para>
/// <list type="bullet">
/// <item><b>Windows exe</b> – a running exe can't delete itself, so the new one is started with
/// <c>--replaced "&lt;old&gt;"</c> and deletes its predecessor once that process has exited.</item>
/// <item><b>Linux AppImage</b> – the AppImage path comes from the <c>APPIMAGE</c> environment variable the
/// runtime sets. The new file is downloaded beside it, made executable, moved over the old one and
/// re-executed.</item>
/// </list>
/// <para>⚠️ macOS is deliberately <b>not</b> swapped: the app is an unsigned <c>.app</c> bundle (a directory,
/// not a file) and replacing it under a running process + Gatekeeper is not something to do silently. There
/// the user is told a new version exists and installs it themselves.</para></summary>
public static class SelfUpdate
{
    /// <summary>True when this build can replace itself at all (see the class remarks).</summary>
    public static bool IsSupported => AppUpdater.CurrentSelfUpdateKind() != AppUpdater.SelfUpdateKind.None;

    /// <summary>Downloads the update and swaps it in. Returns true when the successor was launched – the
    /// caller must then shut this process down promptly so the old file can be replaced/removed.
    /// Returns false (having changed nothing) when the platform can't self-update or the download failed.</summary>
    public static async Task<bool> ApplyAsync(AppUpdater.Available update, IProgress<double>? progress = null)
    {
        switch (AppUpdater.CurrentSelfUpdateKind())
        {
            case AppUpdater.SelfUpdateKind.WindowsExe: return await ApplyWindowsAsync(update, progress);
            case AppUpdater.SelfUpdateKind.LinuxAppImage: return await ApplyAppImageAsync(update, progress);
            default: return false;
        }
    }

    private static async Task<bool> ApplyWindowsAsync(AppUpdater.Available update, IProgress<double>? progress)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current)) return false;
        var dir = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(dir)) return false;

        var downloaded = await AppUpdater.DownloadAsync(update, dir, progress);
        if (downloaded is null || downloaded.Equals(current, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(downloaded)
            {
                Arguments = $"--replaced \"{current}\"",
                UseShellExecute = true,
                WorkingDirectory = dir,
            });
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> ApplyAppImageAsync(AppUpdater.Available update, IProgress<double>? progress)
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(appImage)) return false;
        var dir = Path.GetDirectoryName(appImage);
        if (string.IsNullOrEmpty(dir)) return false;

        var downloaded = await AppUpdater.DownloadAsync(update, dir, progress);
        if (downloaded is null) return false;

        try
        {
            // Make it runnable, then move it over the old AppImage. Move (not copy) so a half-written file
            // can never end up as the installed one.
            Chmod755(downloaded);
            if (!downloaded.Equals(appImage, StringComparison.Ordinal)) File.Move(downloaded, appImage, overwrite: true);
            Process.Start(new ProcessStartInfo(appImage) { UseShellExecute = false, WorkingDirectory = dir });
            return true;
        }
        catch
        {
            try { if (File.Exists(downloaded) && downloaded != appImage) File.Delete(downloaded); } catch { }
            return false;
        }
    }

    private static void Chmod755(string path)
    {
        if (OperatingSystem.IsWindows()) return; // no Unix modes there (and this path is AppImage-only anyway)
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                       | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
        catch { /* no permission – the launch below will tell us */ }
    }

    /// <summary>Deletes the executable an update replaced, retrying while the old process lets go of it.
    /// Called by the successor via the <c>--replaced</c> argument.</summary>
    public static async Task DeleteReplacedAsync(string oldPath)
    {
        for (var i = 0; i < 20; i++)
        {
            try { if (File.Exists(oldPath)) File.Delete(oldPath); return; }
            catch { await Task.Delay(300); }
        }
    }
}
