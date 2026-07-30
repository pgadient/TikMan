using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TikMan.App.Avalonia;

/// <summary>A one-line text prompt. Avalonia has no input box of its own, and several places need one
/// (naming a topology node, and whatever comes next).</summary>
public partial class TextPromptWindow : Window
{
    // ⚠️ FindControl, not the generated fields (see the other windows).
    private readonly TextBlock? _prompt;
    private readonly TextBox? _value;
    private string? _result;

    public TextPromptWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _prompt = this.FindControl<TextBlock>("PromptText");
        _value = this.FindControl<TextBox>("ValueBox");
    }

    /// <summary>Asks for a line of text. Null when cancelled – distinct from "" so a caller can tell an
    /// empty answer from no answer.</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new TextPromptWindow { Title = title };
        if (dialog._prompt is not null) dialog._prompt.Text = prompt;
        if (dialog._value is not null) dialog._value.Text = initial;

        // Focus the box so the answer can just be typed.
        dialog.Opened += (_, _) => dialog._value?.Focus();

        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Accept();
        e.Handled = true;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Accept();
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void Accept()
    {
        _result = _value?.Text ?? "";
        Close();
    }
}
