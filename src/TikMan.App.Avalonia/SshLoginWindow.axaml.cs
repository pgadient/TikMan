using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TikMan.Core.Localization;

namespace TikMan.App.Avalonia;

/// <summary>Asks for an SSH login for <b>one session</b>, the way any SSH client does – so the built-in
/// terminal can reach a device TikMan has no stored password for.
///
/// <para>⚠️ Deliberately NOT the credentials dialog: that one <i>saves</i> what it is given. What is typed
/// here is handed straight to the connection and then dropped – the device store is never touched. Someone
/// opening a shell on a device once should not have to leave a password behind to do it.</para></summary>
public partial class SshLoginWindow : Window
{
    // ⚠️ FindControl, not the x:Name fields – the generator stops emitting those as soon as a view embeds a
    // custom control, and they then silently stay null (the trap that has bitten every window here).
    private readonly TextBlock? _prompt;
    private readonly TextBox? _user;
    private readonly TextBox? _pass;

    public SshLoginWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _prompt = this.FindControl<TextBlock>("PromptText");
        _user = this.FindControl<TextBox>("UserBox");
        _pass = this.FindControl<TextBox>("PassBox");
    }

    public SshLoginWindow(string deviceLabel, string suggestedUser) : this()
    {
        if (_prompt is not null) _prompt.Text = LocalizationManager.T("Av_SshLoginFor", deviceLabel);
        if (_user is not null) _user.Text = suggestedUser;
        // Straight to the password when a username is already known – the usual case.
        Opened += (_, _) =>
        {
            if (suggestedUser.Length > 0) _pass?.Focus();
            else _user?.Focus();
        };
    }

    /// <summary>Shows the dialog and returns what was entered, or null when it was cancelled.</summary>
    public static async Task<(string User, string Password)?> AskAsync(Window owner, string deviceLabel,
        string suggestedUser)
    {
        var dlg = new SshLoginWindow(deviceLabel, suggestedUser);
        return await dlg.ShowDialog<(string User, string Password)?>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close((_user?.Text?.Trim() ?? "", _pass?.Text ?? ""));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Close((_user?.Text?.Trim() ?? "", _pass?.Text ?? ""));
        e.Handled = true;
    }
}
