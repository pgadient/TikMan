using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using TikMan.Core.Fleet;
using TikMan.Core.Localization;

namespace TikMan.App.Avalonia;

/// <summary>What the login dialog returns: the credentials, plus how the device should be reached.
/// Null when the dialog was cancelled.</summary>
/// <param name="Password">Empty clears the stored login.</param>
/// <param name="Method">Auto leaves the ports at their defaults.</param>
/// <param name="Port">Ignored for <see cref="FleetService.ConnectMethod.Auto"/>.</param>
public sealed record LoginResult(string User, string Password, FleetService.ConnectMethod Method, int Port);

/// <summary>Sets the login (and connection) for one device – or for a whole marked set at once.
/// <para>The bulk case gets an amber banner naming the count, because writing one password onto several
/// devices is not something to discover afterwards.</para>
/// <para>The connection row exists because a device whose web interface listens on a non-standard port
/// could not be used at all: the defaults assume 443/80, so the login went to a port with nothing on it.</para></summary>
public partial class LoginWindow : Window
{
    // ⚠️ FindControl, not the x:Name fields: the generator stops emitting the typed fields as soon as a view
    // embeds a custom control, and they then silently stay null. Uniform here so that can't bite later.
    private readonly TextBlock? _prompt;
    private readonly TextBox? _user;
    private readonly TextBox? _pass;
    private readonly Border? _bulkBanner;
    private readonly TextBlock? _bulkText;
    private readonly TextBlock? _testResult;
    private readonly Button? _testButton;
    private readonly TextBlock? _vendorText;
    private readonly ComboBox? _methodBox;
    private readonly TextBox? _portBox;
    private readonly TextBlock? _methodHint;

    /// <summary>Set for a single-device edit, so the entered credentials can be tried before saving.</summary>
    private FleetService? _fleet;
    private string _deviceId = "";

    /// <summary>Every method the dialog knows, in the order they are shown.</summary>
    private static readonly FleetService.ConnectMethod[] AllMethods =
    {
        FleetService.ConnectMethod.Auto,
        FleetService.ConnectMethod.Https,
        FleetService.ConnectMethod.Http,
        FleetService.ConnectMethod.Ssh,
    };

    /// <summary>The methods this instance actually offers.
    /// <para>⚠️ Plain HTTP is dropped when the "allow HTTP" setting is off. It sends the password in the
    /// clear, so a switch the user has deliberately turned off must not still be selectable one click away
    /// in the very dialog that takes the password.</para></summary>
    private FleetService.ConnectMethod[] _methods = AllMethods;

    public LoginWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _prompt = this.FindControl<TextBlock>("PromptText");
        _user = this.FindControl<TextBox>("UserBox");
        _pass = this.FindControl<TextBox>("PassBox");
        _bulkBanner = this.FindControl<Border>("BulkBanner");
        _bulkText = this.FindControl<TextBlock>("BulkText");
        _testResult = this.FindControl<TextBlock>("TestResult");
        _testButton = this.FindControl<Button>("TestButton");
        _vendorText = this.FindControl<TextBlock>("VendorText");
        _methodBox = this.FindControl<ComboBox>("MethodBox");
        _portBox = this.FindControl<TextBox>("PortBox");
        _methodHint = this.FindControl<TextBlock>("MethodHint");

        FillMethods();
    }

    private void FillMethods()
    {
        if (_methodBox is null) return;
        var keep = SelectedMethod;
        _methodBox.ItemsSource = _methods.Select(MethodLabel).ToList();
        _methodBox.SelectedIndex = Math.Max(0, Array.IndexOf(_methods, keep));
    }

    private static string MethodLabel(FleetService.ConnectMethod m) => m switch
    {
        FleetService.ConnectMethod.Https => "HTTPS",
        FleetService.ConnectMethod.Http => LocalizationManager.T("Av_MethodHttp"),
        FleetService.ConnectMethod.Ssh => "SSH",
        _ => LocalizationManager.T("Av_MethodAuto"),
    };

    /// <param name="deviceLabel">Device name, or a summary when several are being edited.</param>
    /// <param name="initialUser">Username to prefill.</param>
    /// <param name="deviceCount">0 or 1 for a single device; &gt;1 shows the bulk warning.</param>
    /// <param name="fleet">Supplied for a single device so the credentials can be tested.</param>
    /// <param name="deviceId">The device the test runs against.</param>
    /// <param name="vendorLine">What TikMan detected – shown so the user can sanity-check it.</param>
    /// <param name="vendor">The identified vendor. Used to drop transports the vendor cannot speak – see
    /// <see cref="TikMan.Core.Discovery.VendorSupport.IsSshOnly"/>.</param>
    public LoginWindow(string deviceLabel, string initialUser, int deviceCount = 0,
        FleetService? fleet = null, string deviceId = "", string vendorLine = "", string vendor = "") : this()
    {
        if (_prompt is not null) _prompt.Text = LocalizationManager.T("Av_LoginFor", deviceLabel);
        if (_user is not null) _user.Text = initialUser;

        _fleet = fleet;
        _deviceId = deviceId;

        // ⚠️ SSH-only vendors get exactly one entry. A TP-Link switch answers on 80/443, so the web options
        // look plausible – but they serve no facts without a session TikMan cannot open, so choosing one
        // configures a login that can only ever fail silently.
        if (TikMan.Core.Discovery.VendorSupport.IsSshOnly(vendor))
        {
            _methods = new[] { FleetService.ConnectMethod.Ssh };
            FillMethods();
        }
        // Plain HTTP disappears from the list unless it has been allowed in the settings.
        else if (fleet is not null && !fleet.HttpAllowed)
        {
            _methods = AllMethods.Where(m => m != FleetService.ConnectMethod.Http).ToArray();
            FillMethods();
        }

        // ⚠️ The REAL stored password, not a placeholder. With a placeholder, saving without touching the
        // field wrote the placeholder as the password, and an emptied field could not be told from an
        // untouched one – so "remove this login" was unreachable. Single device only: a bulk edit has no
        // one password to show, and pre-filling one device's onto the others would be a quiet disaster.
        if (fleet is not null && deviceId.Length > 0 && deviceCount <= 1 && _pass is not null)
            _pass.Text = fleet.PasswordOf(deviceId);

        if (_vendorText is not null) _vendorText.Text = vendorLine;

        if (deviceCount > 1 && _bulkBanner is not null && _bulkText is not null)
        {
            _bulkText.Text = LocalizationManager.T("Av_LoginBulkWarn", deviceCount);
            _bulkBanner.IsVisible = true;
        }

        // Preselect what the device is already set to, so opening the dialog doesn't quietly reset it.
        if (fleet is not null && deviceId.Length > 0 && _methodBox is not null)
        {
            var (method, port) = fleet.ConnectionOf(deviceId);
            _methodBox.SelectedIndex = Math.Max(0, Array.IndexOf(_methods, method));
            if (_portBox is not null && port > 0) _portBox.Text = port.ToString();
        }

        // Testing one password against a whole marked set would produce N answers; showing one of them
        // would be misleading, so the button only exists for a single device.
        if (_testButton is not null) _testButton.IsVisible = fleet is not null && deviceCount <= 1;
        UpdateMethodState();
    }

    private FleetService.ConnectMethod SelectedMethod =>
        _methodBox is { SelectedIndex: >= 0 } && _methodBox.SelectedIndex < _methods.Length
            ? _methods[_methodBox.SelectedIndex]
            : FleetService.ConnectMethod.Auto;

    private void OnMethodChanged(object? sender, SelectionChangedEventArgs e) => UpdateMethodState();

    /// <summary>The standard port per method, and the set of them – a value from this set is treated as
    /// "TikMan put that there", so switching method may replace it.</summary>
    private static string DefaultPortFor(FleetService.ConnectMethod m) => m switch
    {
        FleetService.ConnectMethod.Https => "443",
        FleetService.ConnectMethod.Http => "80",
        _ => "22",
    };

    private static readonly HashSet<string> DefaultPorts = new() { "443", "80", "22" };

    /// <summary>Keeps the port box and the hint in step with the chosen method.</summary>
    private void UpdateMethodState()
    {
        var method = SelectedMethod;
        if (_portBox is not null)
        {
            _portBox.IsEnabled = method != FleetService.ConnectMethod.Auto;

            // ⚠️ Follow the method, not just fill an empty box. Switching HTTPS → SSH used to leave "443"
            // sitting there, which is a wrong port presented as if TikMan had chosen it. A port the user
            // typed is left alone – only a default (or an empty box) is replaced.
            if (method != FleetService.ConnectMethod.Auto)
            {
                var current = _portBox.Text?.Trim() ?? "";
                if (current.Length == 0 || DefaultPorts.Contains(current))
                    _portBox.Text = DefaultPortFor(method);
            }
        }

        if (_methodHint is null) return;
        _methodHint.Text = method switch
        {
            FleetService.ConnectMethod.Auto => LocalizationManager.T("Av_MethodAutoHint"),
            FleetService.ConnectMethod.Http => LocalizationManager.T("Av_MethodHttpHint"),
            _ => "",
        };
        // Plain HTTP sends the password in the clear – the hint says so, in warning colour.
        _methodHint.Foreground = method == FleetService.ConnectMethod.Http
            ? new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))
            : Brushes.Gray;
    }

    /// <summary>Tries the typed credentials against the device and reports what came back. Nothing is
    /// stored: this is a probe, and the password is discarded when the call returns.</summary>
    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        if (_fleet is null || _testResult is null) return;

        // Apply the connection choice first – testing HTTPS:443 when the user just typed 8443 would
        // report a failure that says nothing about the credentials.
        _fleet.SetConnection(_deviceId, SelectedMethod, ParsePort());

        _testResult.IsVisible = true;
        _testResult.Text = LocalizationManager.T("Av_TestConnecting");
        _testResult.Foreground = Brushes.Gray;
        if (_testButton is not null) _testButton.IsEnabled = false;
        try
        {
            var result = await _fleet.TestLoginAsync(_deviceId, _user?.Text?.Trim() ?? "", _pass?.Text ?? "");
            _testResult.Text = result.Ok
                ? LocalizationManager.T("Av_TestOk", result.Message)
                : LocalizationManager.T("Av_TestFailed", result.Message);
            _testResult.Foreground = result.Ok
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }
        finally { if (_testButton is not null) _testButton.IsEnabled = true; }
    }

    private int ParsePort() =>
        int.TryParse(_portBox?.Text?.Trim(), out var p) ? p : 0;

    /// <summary>Enter in either credential field saves, the same as clicking Save.</summary>
    private void OnFieldKey(object? sender, global::Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != global::Avalonia.Input.Key.Enter) return;
        e.Handled = true;
        OnSave(sender, e);
    }

    private void OnSave(object? sender, RoutedEventArgs e) =>
        Close(new LoginResult(_user?.Text?.Trim() ?? "", _pass?.Text ?? "", SelectedMethod, ParsePort()));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
