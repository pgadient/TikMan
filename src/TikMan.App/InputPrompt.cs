using System.Windows;
using System.Windows.Controls;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>A tiny modal single-line text prompt, built in code so it needs no extra XAML. Returns the
/// trimmed input, or null when cancelled.</summary>
internal static class InputPrompt
{
    public static string? Show(Window owner, string title, string prompt)
    {
        var box = new TextBox { Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = T("Set_Save"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), MinWidth = 80 };
        var cancel = new Button { Content = T("Set_Cancel"), IsCancel = true, Padding = new Thickness(16, 4, 16, 4), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            Owner = owner,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => { dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => box.Focus();

        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
