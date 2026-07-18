using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TikMan.App.Avalonia;

/// <summary>The result of the login dialog: the user + password to store for a device (password empty
/// clears the login). Null when the dialog was cancelled.</summary>
public sealed record LoginResult(string User, string Password);

public partial class LoginWindow : Window
{
    public LoginWindow() => AvaloniaXamlLoader.Load(this);

    public LoginWindow(string deviceLabel, string initialUser) : this()
    {
        PromptText.Text = $"Für Gerät: {deviceLabel}";
        UserBox.Text = initialUser;
    }

    private void OnSave(object? sender, RoutedEventArgs e) =>
        Close(new LoginResult(UserBox.Text?.Trim() ?? "", PassBox.Text ?? ""));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
