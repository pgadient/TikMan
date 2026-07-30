using Avalonia;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

internal static class Program
{

    // Avalonia's classic desktop entry point. [STAThread] is required on Windows.
    [STAThread]
    public static void Main(string[] args)
    {
        // Launched by a just-updated build to clean up the file it replaced: a running executable can't
        // delete itself, so the successor does it once the old process has exited.
        if (args.Length >= 2 && args[0] == "--replaced")
            _ = SelfUpdate.DeleteReplacedAsync(args[1]);

        // Linux AppImage only: put TikMan in the application grid so it can be started with a click.
        // Modern GNOME refuses to launch a bare executable from the file manager, so without this the
        // AppImage can only be run from a terminal. No-op everywhere else.
        DesktopIntegration.EnsureRegistered();


        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "av-crash.txt"), ex.ToString()); throw; }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        return SoftwareRenderingRequested() ? builder.WithSoftwareRendering() : builder;
    }

    /// <summary>Whether to skip GPU rendering: the stored setting, or a one-off
    /// <c>--software-render</c> / <c>TIKMAN_SOFTWARE_RENDER=1</c>.
    /// <para>Why this exists: Avalonia renders through the GPU (Metal on macOS, OpenGL elsewhere). On a
    /// machine whose graphics drivers are patched in rather than native – an unsupported Mac running a newer
    /// macOS via OpenCore Legacy Patcher is the case in hand – a GPU render loop can take the whole window
    /// server down with it, which looks like the OS freezing rather than an app crashing. Software rendering
    /// costs some smoothness and fixes that outright.</para>
    /// <para>⚠️ A one-off flag <b>persists itself</b>. Someone who had to reach for it did so because the
    /// machine was unusable otherwise; making them pass it on every launch (or find the setting in a UI they
    /// could not get to) would be the wrong way round. Turning it back off is a checkbox.</para></summary>
    private static bool SoftwareRenderingRequested()
    {
        var forced =
            Environment.GetEnvironmentVariable("TIKMAN_SOFTWARE_RENDER") is "1" or "true" ||
            Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--software-render", StringComparison.OrdinalIgnoreCase));

        try
        {
            var appData = DeviceStore.Load();
            if (forced && !appData.SoftwareRendering)
            {
                appData.SoftwareRendering = true;
                DeviceStore.Save(appData);
            }
            return forced || appData.SoftwareRendering;
        }
        catch
        {
            // Settings unreadable – honour the explicit request anyway. This is the switch people reach for
            // when nothing else works; it must not depend on the rest being healthy.
            return forced;
        }
    }
}
