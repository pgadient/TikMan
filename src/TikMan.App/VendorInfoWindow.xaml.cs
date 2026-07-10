using System.Windows;

namespace TikMan.App;

/// <summary>Shows the full IEEE OUI record (company + address) for a MAC, copyable.</summary>
public partial class VendorInfoWindow : Window
{
    public VendorInfoWindow(string entry)
    {
        InitializeComponent();
        EntryBox.Text = entry;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(EntryBox.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }
}
