using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TikMan.App.Avalonia;

/// <summary>Registers the running AppImage with the Linux desktop, so TikMan shows up in the application
/// grid with its icon and can be launched or pinned like any installed app.
/// <para><b>Why this is needed at all:</b> a raw AppImage is just an executable file, and since Nautilus 42
/// (Ubuntu 22.04+) GNOME deliberately no longer launches executables on double-click – nor does a
/// browser-downloaded file carry the executable bit. So "download it and double-click" simply does not work
/// on a modern desktop, no matter how the AppImage is built. A <c>.desktop</c> entry is the supported way
/// in, and writing one is what tools like AppImageLauncher do.</para>
/// <para>Runs only when <c>APPIMAGE</c> is set (i.e. actually running as an AppImage), does nothing on
/// Windows/macOS, and never throws: failing to register is a cosmetic problem, not a reason to not start.
/// Set <c>TIKMAN_NO_DESKTOP_INTEGRATION=1</c> to skip it.</para></summary>
public static class DesktopIntegration
{
    private const string EntryName = "tikman.desktop";

    /// <summary>Writes (or refreshes) the desktop entry and icon. Safe to call on every start – it rewrites
    /// only when something actually changed, so moving the AppImage fixes the entry on the next launch.</summary>
    public static void EnsureRegistered()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            if (Environment.GetEnvironmentVariable("TIKMAN_NO_DESKTOP_INTEGRATION") is { Length: > 0 }) return;

            // Set by the AppImage runtime to the path of the .AppImage file itself – that, not the extracted
            // binary under /tmp, is what the launcher has to point at (the mount is gone after exit).
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrWhiteSpace(appImage) || !File.Exists(appImage)) return;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length == 0) return;

            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x : Path.Combine(home, ".local", "share");

            var iconPath = WriteIcon(dataHome);
            WriteEntry(Path.Combine(dataHome, "applications"), appImage, iconPath);
        }
        catch { /* integration is a nicety – never let it stop the app from starting */ }
    }

    /// <summary>Copies the icon into the hicolor theme so the desktop can find it by name. Returns the icon
    /// name to reference, or the empty string when no icon could be placed.</summary>
    private static string WriteIcon(string dataHome)
    {
        try
        {
            var dir = Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "tikman.png");

            // Inside a running AppImage the payload is mounted at $APPDIR; the packaging script puts the
            // icon at its root. Fall back to the icon embedded in this assembly's own directory.
            var appDir = Environment.GetEnvironmentVariable("APPDIR") ?? "";
            var candidates = new[]
            {
                Path.Combine(appDir, "tikman.png"),
                Path.Combine(appDir, "usr", "share", "icons", "hicolor", "256x256", "apps", "tikman.png"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? "") ?? "", "tikman.png"),
            };

            foreach (var c in candidates)
            {
                if (c.Length == 0 || !File.Exists(c)) continue;
                if (!File.Exists(target) || new FileInfo(c).Length != new FileInfo(target).Length)
                    File.Copy(c, target, overwrite: true);
                return "tikman";
            }
            return File.Exists(target) ? "tikman" : "";
        }
        catch { return ""; }
    }

    private static void WriteEntry(string appsDir, string appImage, string iconName)
    {
        Directory.CreateDirectory(appsDir);
        var path = Path.Combine(appsDir, EntryName);

        // Exec is quoted: AppImages are routinely kept in paths with spaces ("~/Downloads/My Apps/…").
        // %U lets the desktop pass file arguments; TryExec makes the entry hide itself automatically if the
        // AppImage is later deleted, instead of leaving a launcher that errors.
        var contents =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=TikMan\n" +
            "Comment=Network device dashboard (MikroTik & multi-vendor)\n" +
            $"Exec=\"{appImage}\" %U\n" +
            $"TryExec={appImage}\n" +
            (iconName.Length > 0 ? $"Icon={iconName}\n" : "") +
            "Categories=Network;System;\n" +
            "Terminal=false\n" +
            "StartupWMClass=TikMan\n";

        // Only write when the content actually differs – avoids touching the file (and the desktop's
        // menu-rebuild) on every single start.
        if (File.Exists(path) && File.ReadAllText(path) == contents) return;
        File.WriteAllText(path, contents);
    }
}
