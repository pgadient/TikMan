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

/// <summary>Dialog for adding a device (existing == null) or setting credentials for one or several
/// existing devices. Vendor, name and connection scheme are detected automatically, so the dialog
/// only asks for the address (when adding), user, password and SSH port.</summary>
public partial class DeviceEditWindow : Window
{
    private readonly Device? _existing;
    private readonly IReadOnlyList<Device>? _multi;

    /// <summary>The created device after OK (add flow only).</summary>
    public Device? Result { get; private set; }

    /// <summary>Sets credentials for several existing devices at once (applied 1:1 to all).</summary>
    public DeviceEditWindow(IReadOnlyList<Device> devices)
    {
        if (devices is null || devices.Count == 0)
            throw new ArgumentException("Multi-edit needs at least one device.", nameof(devices));
        InitializeComponent();
        _multi = devices;
        var first = devices[0];

        Title = T("De_TitleEditMulti");
        MultiBanner.Visibility = Visibility.Visible;
        RowAddress.Height = new GridLength(0); // address stays per-device
        UserBox.Text = first.Username;
        SshPortBox.Text = first.SshPort.ToString();
        PasswordHint.Visibility = Visibility.Visible;
        TestButton.IsEnabled = false; // no single host to test against
    }

    public DeviceEditWindow(Device? existing, bool defaultIgnoreCert = true)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            Title = $"{T("De_TitleEdit")} – {existing.Host}"; // address in the title, not the body
            RowAddress.Height = new GridLength(0);
            UserBox.Text = existing.Username;
            SshPortBox.Text = existing.SshPort.ToString();
            PasswordHint.Visibility = Visibility.Visible;
        }
        else
        {
            Title = T("De_TitleAdd");
            _defaultIgnoreCert = defaultIgnoreCert;
        }
    }

    private readonly bool _defaultIgnoreCert = true;

    /// <summary>The SSH port from its box, or the current value when the box is empty/invalid.</summary>
    private int ParseSshPort(int fallback) =>
        int.TryParse(SshPortBox.Text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : fallback;

    /// <summary>Builds the device for the add flow (address + credentials; scheme defaults to HTTPS,
    /// which the refresh downgrades to HTTP only when the user allowed it in Settings).</summary>
    private Device? BuildDevice(out string error)
    {
        error = "";
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { error = T("De_ErrAddressEmpty"); return null; }

        var device = _existing ?? new Device { UseHttps = true, Port = 443, IgnoreCertErrors = _defaultIgnoreCert };
        device.Host = host;
        device.Username = UserBox.Text.Trim();
        device.SshPort = ParseSshPort(device.SshPort);
        if (PasswordBox.Password.Length > 0 || _existing is null)
            device.EncryptedPassword = CredentialProtector.Protect(PasswordBox.Password);
        return device;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var device = BuildDevice(out var error);
        if (device is null) { ShowTestResult(error, ok: false); return; }

        TestButton.IsEnabled = false;
        ShowTestResult(T("De_Connecting"), ok: true);
        try
        {
            using var client = RouterOsClient.For(device, CredentialProtector.Unprotect(device.EncryptedPassword));
            var resource = await client.GetSystemResourceAsync();
            var identity = await client.GetIdentityAsync();
            ShowTestResult(T("De_TestOk", identity, resource.BoardName, resource.Version), ok: true);
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
        catch { return false; }
    }

    private void ShowTestResult(string text, bool ok)
    {
        TestResultText.Text = text;
        TestResultText.Foreground = ok ? Brushes.ForestGreen : Brushes.Firebrick;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_multi is not null) { ApplyToAll(); DialogResult = true; return; }

        if (_existing is not null)
        {
            _existing.Username = UserBox.Text.Trim();
            _existing.SshPort = ParseSshPort(_existing.SshPort);
            if (PasswordBox.Password.Length > 0)
                _existing.EncryptedPassword = CredentialProtector.Protect(PasswordBox.Password);
            DialogResult = true;
            return;
        }

        var device = BuildDevice(out var error);
        if (device is null) { ShowTestResult(error, ok: false); return; }
        Result = device;
        DialogResult = true;
    }

    /// <summary>Applies the same credentials/SSH port 1:1 to every selected device (password only
    /// when a new one was typed).</summary>
    private void ApplyToAll()
    {
        var user = UserBox.Text.Trim();
        var sshPort = ParseSshPort(22);
        var newPassword = PasswordBox.Password.Length > 0 ? CredentialProtector.Protect(PasswordBox.Password) : null;
        foreach (var d in _multi!)
        {
            d.Username = user;
            d.SshPort = sshPort;
            if (newPassword is not null) d.EncryptedPassword = newPassword;
        }
    }
}
