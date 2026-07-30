using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TikMan.App.Avalonia;

/// <summary>A yes/no prompt for actions that cannot be undone. Avalonia has no built-in message box, and
/// deleting a saved device list on a single click is exactly the kind of thing that needs one.
/// <para>Cancel is the default button on purpose: an accidental Enter must never confirm.</para></summary>
public partial class ConfirmWindow : Window
{
    // ⚠️ FindControl, not the generated x:Name fields – see the other windows.
    private readonly TextBlock _message;
    private readonly Button _confirmButton;
    private bool _confirmed;

    public ConfirmWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _message = this.FindControl<TextBlock>("MessageText")!;
        _confirmButton = this.FindControl<Button>("ConfirmButton")!;
    }

    /// <summary>Asks the question and returns true only if the user actively confirmed. Closing the window
    /// any other way (Escape, the title-bar X) counts as "no".</summary>
    public static async Task<bool> AskAsync(Window owner, string message, string confirmLabel)
    {
        var dialog = new ConfirmWindow();
        dialog._message.Text = message;
        dialog._confirmButton.Content = confirmLabel;
        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) { _confirmed = true; Close(); }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
