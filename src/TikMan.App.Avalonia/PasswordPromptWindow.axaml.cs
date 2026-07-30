using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TikMan.App.Avalonia;

/// <summary>A one-field password prompt. Closes with the entered string, or null when cancelled.</summary>
public partial class PasswordPromptWindow : Window
{
    public PasswordPromptWindow() => AvaloniaXamlLoader.Load(this);

    public PasswordPromptWindow(string prompt) : this()
    {
        // ⚠️ FindControl, not x:Name fields – see LoginWindow.
        if (this.FindControl<TextBlock>("PromptText") is { } text) text.Text = prompt;
        Opened += (_, _) => this.FindControl<TextBox>("PassBox")?.Focus();
    }

    /// <summary>⚠️ The box is looked up by name on every use, never through the <c>x:Name</c> field.
    ///
    /// <para>The field is <b>null</b> here: it is the generated <c>InitializeComponent()</c> that assigns
    /// the named-control fields, and this window loads its XAML with <c>AvaloniaXamlLoader.Load(this)</c>
    /// directly – which builds the tree and populates the name scope, but never touches the fields. The
    /// constructor above already knew that; the two handlers below did not, so entering a VNC password and
    /// pressing OK threw a NullReferenceException that took the whole process down. Nothing warns about it:
    /// it compiles, the window opens, and it only fails once someone confirms the dialog.</para></summary>
    private string EnteredPassword => this.FindControl<TextBox>("PassBox")?.Text ?? "";

    private void OnOk(object? sender, RoutedEventArgs e) => Close(EnteredPassword);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Close(EnteredPassword); e.Handled = true; }
    }
}
