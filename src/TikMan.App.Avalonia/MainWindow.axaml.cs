using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TikMan.App.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
