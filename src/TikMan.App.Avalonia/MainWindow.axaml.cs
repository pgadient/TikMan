using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TikMan.App.Avalonia;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm = new();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _vm;
    }

    private void OnScanClick(object? sender, RoutedEventArgs e) => _vm.Scan();
}
