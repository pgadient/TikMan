using System.IO;
using System.Windows;
using System.Windows.Threading;
using TikMan.App.Localization;
using TikMan.Core.Storage;

namespace TikMan.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        // Load settings and set the language before the window appears,
        // so the UI is built in the correct language right from the start.
        var data = DeviceStore.Load();
        LocalizationManager.Instance.Apply(data.Language);

        var main = new MainWindow(data);
        MainWindow = main;
        main.Show();
    }

    /// <summary>Last line of defense: log and display the error instead of terminating
    /// the app without any notice.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = "";
        try
        {
            Directory.CreateDirectory(DeviceStore.StorageDirectory);
            logPath = Path.Combine(DeviceStore.StorageDirectory, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\r\n\r\n");
        }
        catch { /* Logging must not break the handler */ }

        MessageBox.Show(
            $"Unexpected error:\n\n{e.Exception.Message}\n\n" +
            $"The app keeps running; details are in\n{logPath}",
            "TikMan", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
