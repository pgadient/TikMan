using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using TikMan.Core.Localization;
using TikMan.Core.Models;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the saved language and appearance before any window is built, so the UI comes up localized
        // and in the right theme rather than flashing the default first. The shared LocalizationManager
        // (Core) drives both this app and the WPF client from the same tables.
        try
        {
            var data = DeviceStore.Load();
            LocalizationManager.Instance.Apply(data.Language);
            ApplyTheme(data.Theme);
        }
        catch { /* fall back to defaults */ }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Switches the light/dark appearance. <see cref="AppTheme.System"/> maps to Avalonia's
    /// Default variant, which keeps following the OS setting instead of freezing whatever it is right now.
    /// Takes effect immediately – every control resolves its brushes from the active variant.</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
