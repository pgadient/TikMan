using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TikMan.Core.Discovery;
using TikMan.Core.Localization;

namespace TikMan.App.Avalonia;

/// <summary>Shows the full IEEE OUI registration behind a MAC – the registrant's name and postal address,
/// verbatim from the public list.
/// <para>Worth having as its own window rather than a tooltip: when a device reports nothing about itself,
/// who registered its MAC block is often the only clue to what it is, and the address distinguishes an ODM
/// from the brand that resold the hardware.</para></summary>
public partial class VendorInfoWindow : Window
{
    // ⚠️ FindControl, not the generated fields (see the other windows).
    private readonly TextBlock? _header;
    private readonly TextBox? _record;

    public VendorInfoWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _header = this.FindControl<TextBlock>("HeaderText");
        _record = this.FindControl<TextBox>("RecordBox");
    }

    public VendorInfoWindow(string mac, string deviceName) : this()
    {
        if (_header is not null)
            _header.Text = LocalizationManager.T("Av_VendorInfoFor", deviceName, mac);

        var entry = OuiLookup.GetFullEntry(mac);
        if (_record is not null)
            _record.Text = entry.Length > 0 ? entry : LocalizationManager.T("Av_VendorInfoNone");
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var text = _record?.Text ?? "";
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
