using System.Windows;
using System.Windows.Input;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>Modal progress while the app updates itself. Shown for the download only: it is the one
/// thing happening, the main window is about to be replaced anyway, and a plain status line made the
/// wait look like a hang. Has no cancel and no close button on purpose – there is nothing to go back
/// to mid-download, and the update is over in seconds.</summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(string version, string releaseName)
    {
        InitializeComponent();
        SubText.Text = releaseName.Length > 0 ? T("Upd_ProgressSubNamed", version, releaseName)
                                              : T("Upd_ProgressSub", version);
        SetProgress(0);
    }

    /// <summary><paramref name="fraction"/> is 0..1, as reported by the downloader.</summary>
    public void SetProgress(double fraction)
    {
        var f = Math.Clamp(fraction, 0, 1);
        Bar.Value = f;
        PercentText.Text = $"{f * 100:0} %";
    }

    /// <summary>Alt+F4 must not leave a half-written exe behind: only <see cref="CloseWhenReady"/>
    /// gets to close this window, once the download has ended one way or the other.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = !_allowClose;
        base.OnClosing(e);
    }

    private bool _allowClose;
    private bool _closeRequested;

    /// <summary>Ends the dialog. Safe before it is even on screen: a download that fails on the spot
    /// (bad URL, no network) completes before ShowDialog() has run, and closing a window that was
    /// never shown would make that ShowDialog() throw. So we remember and close on Loaded instead.</summary>
    public void CloseWhenReady()
    {
        _allowClose = true;
        _closeRequested = true;
        if (IsLoaded) Close();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_closeRequested) Close();
    }
}
