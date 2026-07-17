using Avalonia;

namespace TikMan.App.Avalonia;

internal static class Program
{
    // Avalonia's classic desktop entry point. [STAThread] is required on Windows.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
