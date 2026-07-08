using System.Net;
using System.Net.Http;
using System.Windows;
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

    /// <summary>The created/edited device after OK.</summary>
    public Device? Result { get; private set; }

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
            PasswordHint.Visibility = Visibility.Visible;
        }
        else
        {
            Title = T("De_TitleAdd");
        }
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
        var device = BuildDevice(out var error);
        if (device is null)
        {
            ShowTestResult(error, ok: false);
            return;
        }
        Result = device;
        DialogResult = true;
    }
}
