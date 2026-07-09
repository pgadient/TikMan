using System.Linq;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TikMan.Core.Api;
using TikMan.Core.Models;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>Dialog for creating (existing == null) or editing a device.</summary>
public partial class DeviceEditWindow : Window
{
    private readonly Device? _existing;
    private readonly IReadOnlyList<Device>? _multi;

    /// <summary>The created/edited device after OK.</summary>
    public Device? Result { get; private set; }

    /// <summary>Edits many devices at once: name and address stay per-device, everything else below
    /// is applied to all of them (password only when a new one is entered).</summary>
    public DeviceEditWindow(IReadOnlyList<Device> devices)
    {
        InitializeComponent();
        _multi = devices;
        var first = devices[0];

        Title = T("De_TitleEditMulti");
        MultiBanner.Visibility = Visibility.Visible;
        VendorPanel.Visibility = Visibility.Collapsed; // vendor/model stay per-device in a bulk edit
        RowName.Height = new GridLength(0); // hide Name and Address rows
        RowHost.Height = new GridLength(0);

        PortBox.Text = first.Port.ToString();
        HttpsCheck.IsChecked = first.UseHttps;
        IgnoreCertCheck.IsChecked = first.IgnoreCertErrors;
        UserBox.Text = first.Username;
        MonitoringCheck.IsChecked = first.MonitoringEnabled;
        NotesBox.Text = first.Notes;
        SelectChannel(first.UpdateChannel);
        PasswordHint.Visibility = Visibility.Visible;
        TestButton.IsEnabled = false; // no single host to test against
    }

    public DeviceEditWindow(Device? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            Title = T("De_TitleEdit");
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port.ToString();
            HttpsCheck.IsChecked = existing.UseHttps;
            IgnoreCertCheck.IsChecked = existing.IgnoreCertErrors;
            UserBox.Text = existing.Username;
            MonitoringCheck.IsChecked = existing.MonitoringEnabled;
            NotesBox.Text = existing.Notes;
            SelectChannel(existing.UpdateChannel);
            ModelBox.Text = existing.Model;
            HwRevBox.Text = existing.HardwareRevision;
            VendorCombo.SelectedValue = existing.Vendor.ToString();
            if (VendorCombo.SelectedValue is null) VendorCombo.SelectedIndex = 0;
            PasswordHint.Visibility = Visibility.Visible;
        }
        else
        {
            Title = T("De_TitleAdd");
            ChannelCombo.SelectedIndex = 0; // (Default)
            VendorCombo.SelectedIndex = 0;  // MikroTik
        }
    }

    /// <summary>Toggles the vendor-specific fields: TP-Link shows model/revision and hides the
    /// RouterOS update channel; the default port follows (443 HTTPS ↔ 22 SSH).</summary>
    private void VendorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TpLinkPanel is null) return; // fires before the visual tree exists
        var tpLink = (VendorCombo.SelectedValue as string) == nameof(DeviceVendor.TpLink);
        TpLinkPanel.Visibility = tpLink ? Visibility.Visible : Visibility.Collapsed;
        RowChannel.Height = tpLink ? new GridLength(0) : new GridLength(32);
        if (tpLink && PortBox.Text is "443") { HttpsCheck.IsChecked = false; PortBox.Text = "22"; }
        else if (!tpLink && PortBox.Text is "22") { HttpsCheck.IsChecked = true; PortBox.Text = "443"; }
    }

    private void SelectChannel(string channel)
    {
        foreach (var item in ChannelCombo.Items.OfType<ComboBoxItem>())
            if ((item.Tag as string ?? "") == channel) { ChannelCombo.SelectedItem = item; return; }
        ChannelCombo.SelectedIndex = 0;
    }

    private void HttpsCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Fires already during InitializeComponent (IsChecked="True" in the XAML) – PortBox doesn't exist yet at that point
        if (PortBox is null) return;

        // Switch the default port along, as long as the other default is still set
        if (HttpsCheck.IsChecked == true && PortBox.Text == "80") PortBox.Text = "443";
        else if (HttpsCheck.IsChecked == false && PortBox.Text == "443") PortBox.Text = "80";
    }

    private Device? BuildDevice(out string error)
    {
        error = "";
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            error = T("De_ErrAddressEmpty");
            return null;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            error = T("De_ErrPort");
            return null;
        }

        var device = _existing ?? new Device();
        device.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : host;
        device.Host = host;
        device.Port = port;
        device.UseHttps = HttpsCheck.IsChecked == true;
        device.IgnoreCertErrors = IgnoreCertCheck.IsChecked == true;
        device.Username = UserBox.Text.Trim();
        device.MonitoringEnabled = MonitoringCheck.IsChecked == true;
        device.Notes = NotesBox.Text.Trim();
        device.UpdateChannel = (ChannelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        device.Vendor = Enum.TryParse<DeviceVendor>(VendorCombo.SelectedValue as string, out var vendor) ? vendor : DeviceVendor.MikroTik;
        device.Model = ModelBox.Text.Trim();
        device.HardwareRevision = HwRevBox.Text.Trim();

        if (PasswordBox.Password.Length > 0 || _existing is null)
            device.EncryptedPassword = CredentialProtector.Protect(PasswordBox.Password);

        return device;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var device = BuildDevice(out var error);
        if (device is null)
        {
            ShowTestResult(error, ok: false);
            return;
        }

        TestButton.IsEnabled = false;
        ShowTestResult(T("De_Connecting"), ok: true);
        try
        {
            if (device.Vendor == DeviceVendor.TpLink)
            {
                var facts = await TpLinkSshConnector.GetFactsAsync(device, CredentialProtector.Unprotect(device.EncryptedPassword));
                ShowTestResult(T("De_TestOkTp", facts.Model, facts.HardwareVersion, facts.FirmwareVersion), ok: true);
                if (NameBox.Text.Trim().Length == 0 && facts.Name.Length > 0) NameBox.Text = facts.Name;
                return;
            }

            using var client = RouterOsClient.For(device, CredentialProtector.Unprotect(device.EncryptedPassword));
            var resource = await client.GetSystemResourceAsync();
            var identity = await client.GetIdentityAsync();
            ShowTestResult(T("De_TestOk", identity, resource.BoardName, resource.Version), ok: true);
            if (NameBox.Text.Trim().Length == 0 && identity.Length > 0)
                NameBox.Text = identity;
        }
        catch (Exception ex)
        {
            var text = T("De_TestErr", ErrorText.Describe(ex));
            if (device.UseHttps && ErrorText.IsTlsProblem(ex) && await ProbeHttpRestAsync(device.Host))
                text += T("De_HttpHint");
            ShowTestResult(text, ok: false);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>Checks without credentials whether the REST API responds via HTTP
    /// (401 means: reachable, just wants a login) – nothing secret is sent.</summary>
    private static async Task<bool> ProbeHttpRestAsync(string host)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var resp = await http.GetAsync($"http://{host}/rest/system/identity");
            return resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private void ShowTestResult(string text, bool ok)
    {
        TestResultText.Text = text;
        TestResultText.Foreground = ok ? Brushes.ForestGreen : Brushes.Firebrick;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_multi is not null)
        {
            if (ApplyToAll(out var multiError)) DialogResult = true;
            else ShowTestResult(multiError, ok: false);
            return;
        }

        var device = BuildDevice(out var error);
        if (device is null)
        {
            ShowTestResult(error, ok: false);
            return;
        }
        Result = device;
        DialogResult = true;
    }

    /// <summary>Applies the shared settings (everything except name/address) to every selected
    /// device. The password is only changed when a new one was typed.</summary>
    private bool ApplyToAll(out string error)
    {
        error = "";
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            error = T("De_ErrPort");
            return false;
        }

        var useHttps = HttpsCheck.IsChecked == true;
        var ignoreCert = IgnoreCertCheck.IsChecked == true;
        var user = UserBox.Text.Trim();
        var monitor = MonitoringCheck.IsChecked == true;
        var notes = NotesBox.Text.Trim();
        var channel = (ChannelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var newPassword = PasswordBox.Password.Length > 0 ? CredentialProtector.Protect(PasswordBox.Password) : null;

        foreach (var d in _multi!)
        {
            d.Port = port;
            d.UseHttps = useHttps;
            d.IgnoreCertErrors = ignoreCert;
            d.Username = user;
            d.MonitoringEnabled = monitor;
            d.Notes = notes;
            d.UpdateChannel = channel;
            if (newPassword is not null) d.EncryptedPassword = newPassword;
        }
        return true;
    }
}
