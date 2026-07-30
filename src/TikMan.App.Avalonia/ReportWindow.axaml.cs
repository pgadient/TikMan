using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TikMan.Core.Diagnostics;
using TikMan.Core.Localization;

namespace TikMan.App.Avalonia;

/// <summary>Shown before the issue tracker opens: the action log, what it contains, and the reminder to
/// read it before attaching it.
/// <para>The log is <b>displayed</b> rather than merely mentioned. Telling someone "a log was written to
/// …/tikman-actions.log, please review it" reliably produces a report with an unreviewed log attached —
/// or no log at all. Putting it on screen makes reviewing it the path of least resistance.</para></summary>
public partial class ReportWindow : Window
{
    // ⚠️ FindControl, not the generated fields (see the other windows).
    private readonly TextBox? _logBox;
    private readonly TextBlock? _pathText;
    private readonly MainWindowViewModel? _vm;

    public ReportWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _logBox = this.FindControl<TextBox>("LogBox");
        _pathText = this.FindControl<TextBlock>("PathText");
    }

    public ReportWindow(MainWindowViewModel vm) : this()
    {
        _vm = vm;
        ApplyScrub();

        var path = vm.SaveActionLog();
        if (_pathText is not null)
            _pathText.Text = path.Length > 0
                ? LocalizationManager.T("Av_ReportSavedTo", path)
                : LocalizationManager.T("Av_ReportNotSaved");
    }

    private void OnScrubChanged(object? sender, RoutedEventArgs e) => ApplyScrub();

    /// <summary>Rebuilds the preview from the original log, so the toggle works in both directions.
    ///
    /// <para>⚠️ Always re-reads <see cref="ActionLog.Snapshot"/> rather than un-scrubbing what is on screen.
    /// Pseudonymisation is one-way by design – there is no way back from "name#3" to the name, and trying
    /// to toggle in place would silently lose whatever the user had already deleted by hand.</para></summary>
    private void ApplyScrub()
    {
        if (_logBox is null) return;

        var text = ActionLog.Snapshot();
        if (this.FindControl<CheckBox>("ScrubNames")?.IsChecked == true && _vm is not null)
            text = ActionLog.RedactNames(text, KnownNames());

        _logBox.Text = text;
    }

    /// <summary>Every name TikMan can recognise as identifying: the devices it found, this machine, and the
    /// signed-in user. Not a guess – these are values the app already holds, which is what makes replacing
    /// them exact instead of a heuristic that mangles ordinary words.</summary>
    private IEnumerable<string> KnownNames()
    {
        if (_vm is null) yield break;

        foreach (var d in _vm.DeviceSnapshots)
        {
            if (d.Name.Length > 0) yield return d.Name;
            // The stored username is an account name, and often a person's.
            if (d.User.Length > 0) yield return d.User;
        }

        yield return Environment.MachineName;
        yield return Environment.UserName;
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => _vm?.OpenLogFolder();

    private void OnOpenIssue(object? sender, RoutedEventArgs e) => _vm?.ReportProblem();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var text = _logBox?.Text ?? "";
        if (text.Length == 0) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch { /* another app can hold the clipboard – not worth interrupting for */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
