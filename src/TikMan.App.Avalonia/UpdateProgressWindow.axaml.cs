using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TikMan.Core.Localization;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

/// <summary>Modal progress while the app replaces itself: a big bar, no cancel and no close button – the
/// program is being swapped out underneath, so there is nothing sensible to abort to. It also says thank
/// you, which is the one moment the user is guaranteed to read it.
/// <para>⚠️ Closing follows the WPF client's hard-won rule: a download that fails immediately finishes
/// <i>before</i> ShowDialog has run, and closing a window that was never shown makes ShowDialog throw. So a
/// finish that lands early is remembered and applied in OnOpened instead.</para></summary>
public partial class UpdateProgressWindow : Window
{
    private readonly AppUpdater.Available _update = null!; // set by the real ctor; the previewer ctor never runs an update
    private ProgressBar _bar = null!;
    private TextBlock _detail = null!;
    private bool _finished;

    /// <summary>True when the successor was launched and this process should shut down.</summary>
    public bool Succeeded { get; private set; }

    public UpdateProgressWindow() => AvaloniaXamlLoader.Load(this); // XAML previewer

    public UpdateProgressWindow(AppUpdater.Available update) : this()
    {
        _update = update;
        _bar = this.FindControl<ProgressBar>("Bar")!;
        _detail = this.FindControl<TextBlock>("Detail")!;
        _detail.Text = LocalizationManager.T("Upd_DownloadingWhat", update.Version.ToString(), update.ReleaseName);

        Opened += async (_, _) =>
        {
            if (_finished) { Close(); return; }
            await RunAsync();
        };
    }

    private async Task RunAsync()
    {
        var progress = new Progress<double>(p => _bar.Value = Math.Clamp(p, 0, 1));
        try { Succeeded = await SelfUpdate.ApplyAsync(_update, progress); }
        catch { Succeeded = false; }
        CloseWhenReady();
    }

    /// <summary>Closes now if the window is up, otherwise leaves a note for OnOpened (see the class remarks).</summary>
    private void CloseWhenReady()
    {
        _finished = true;
        if (IsVisible) Dispatcher.UIThread.Post(Close);
    }
}
