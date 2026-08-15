using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TikMan.Core;
using TikMan.Core.Api;
using TikMan.Core.Discovery;
using TikMan.Core.Localization;
using TikMan.Core.Models;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

public partial class SettingsWindow : Window
{
    // The settings this dialog edits. Snapshotted on open so «Abbrechen» reverts the live AppData
    // instance the fleet reads from (reflection keeps the two lists – XAML + here – from drifting).
    private static readonly string[] Fields =
    {
        "Language", "PersistDeviceList", "NoInitialScan",
        "CheckForUpdates", "ExpandRowsByDefault",
        "ShowListInfo", "ShowVncNotice", "ShowContactButtons",
        "SnmpCommunity", "PingTimeoutMs", "PingRetries", "PollIntervalSeconds",
        "AutoRefreshEnabled",
        "AllowHttpFallback", "DefaultIgnoreCertErrors", "UseExternalSshClient", "PreferBuiltInSsh",
        "ExternalSshClientPath", "VlcPath", "WinScpPath", "PassPasswordToExternalClients",
        "WebServerAutoStart", "WebServerUseHttps", "WebServerPort", "WebServerUser", "WebServerCertPath",
        "AutoCheckEnabled", "AutoCheckTime", "SmtpHost", "SmtpPort", "SmtpUser", "SmtpUseTls", "NotifyLevel",
        "MailFrom", "MailTo",
        "SoftwareRendering", "DisableActionLog", "ScanUnpingableHosts", "MaxConcurrentProbes", "CustomPorts",
        "ParallelDeviceReads",
    };

    private readonly AppData _appData = new();
    private readonly Dictionary<string, object?> _original = new();
    // ⚠️ FindControl, not x:Name fields (the recurring Avalonia generator trap) – reliable in the ctor.
    private ComboBox? _languageBox;
    private TextBox? _webPwBox, _smtpPwBox, _certPwBox;
    // Whether the user actually edited each password box. ⚠️ Needed since the boxes are pre-filled with a
    // stand-in: without it, saving would re-encrypt the stand-in and overwrite the real password with it.
    private bool _webPwTouched, _smtpPwTouched, _certPwTouched;

    /// <summary>Pre-fills a password box with a fixed-length stand-in when something is stored, so the field
    /// shows "a password is set" instead of looking empty. The box masks its text, so the stand-in is only
    /// ever seen as five dots. Editing it sets the touched flag – that, not the content, is what decides
    /// whether the stored value is replaced on save.</summary>
    private static void ShowStoredPassword(TextBox? box, string stored, Action onEdited)
    {
        if (box is null) return;
        if (stored.Length > 0) box.Text = "*****";
        // Subscribed AFTER the pre-fill, so setting the stand-in does not itself count as an edit.
        box.TextChanged += (_, _) => onEdited();
    }

    /// <summary>Pre-fills a password box with the <b>decrypted</b> password, for a password the user has to
    /// be able to read back rather than merely confirm exists.
    ///
    /// <para>Returns true when the real value went in – the caller then treats the box as authoritative, so
    /// clearing it clears the stored password. Returns false when the blob would not decrypt (written under
    /// another Windows profile): the stand-in is shown instead, because saving an empty box would otherwise
    /// destroy a password TikMan only failed to <i>read</i>.</para></summary>
    private static bool ShowPlainPassword(TextBox? box, string encrypted, Action onEdited)
    {
        if (box is null) return false;
        var plain = encrypted.Length > 0 ? CredentialProtector.Unprotect(encrypted) : "";
        var authoritative = encrypted.Length == 0 || plain.Length > 0;
        box.Text = authoritative ? plain : "*****";
        box.TextChanged += (_, _) => onEdited();
        return authoritative;
    }
    private TextBlock? _updateStatus, _ouiStatus, _captureStatus;
    private Button? _checkUpdateButton, _ouiButton, _applyUpdateButton;

    public SettingsWindow() => AvaloniaXamlLoader.Load(this); // XAML previewer

    public SettingsWindow(AppData appData)
    {
        AvaloniaXamlLoader.Load(this);
        _appData = appData;
        foreach (var f in Fields)
            if (typeof(AppData).GetProperty(f) is { } p) _original[f] = p.GetValue(appData);

        _languageBox = this.FindControl<ComboBox>("LanguageBox");
        _webPwBox = this.FindControl<TextBox>("WebPwBox");
        _smtpPwBox = this.FindControl<TextBox>("SmtpPwBox");
        _certPwBox = this.FindControl<TextBox>("CertPwBox");
        _updateStatus = this.FindControl<TextBlock>("UpdateStatus");
        _ouiStatus = this.FindControl<TextBlock>("OuiStatus");
        _captureStatus = this.FindControl<TextBlock>("CaptureStatus");
        _checkUpdateButton = this.FindControl<Button>("CheckUpdateButton");
        _ouiButton = this.FindControl<Button>("OuiButton");
        _applyUpdateButton = this.FindControl<Button>("ApplyUpdateButton");

        // ⚠️ The web password comes back in the CLEAR, unlike the two below. It is the password the user
        // types into a browser on another machine, so it has to be readable back – a stand-in makes a
        // forgotten one unrecoverable except by resetting it. Touched is set straight away, so the box is
        // authoritative from here on: clearing it clears the stored password.
        // Fallback: a blob that will not decrypt (written under a different Windows profile) keeps the
        // stand-in, so saving cannot silently destroy a password TikMan merely failed to read.
        _webPwTouched = ShowPlainPassword(_webPwBox, _appData.WebServerEncryptedPassword,
            () => _webPwTouched = true);
        ShowStoredPassword(_smtpPwBox, _appData.SmtpEncryptedPassword, () => _smtpPwTouched = true);
        ShowStoredPassword(_certPwBox, _appData.WebServerCertPassword, () => _certPwTouched = true);

        // Offer the installed VLC when the field is still empty. ⚠️ Only a path that actually exists is
        // filled in, and only into an empty field – overwriting a chosen player, or presenting a guess as a
        // setting, would both be worse than leaving it blank.
        if (appData.VlcPath is not { Length: > 0 } && Launchers.FindVlc() is { } vlc)
            appData.VlcPath = vlc;

        // And the SFTP client, on every platform – WinSCP, FileZilla or Cyberduck, whichever is installed.
        // Same rule: an existing path, into an empty field only.
        if (appData.WinScpPath is not { Length: > 0 } && Launchers.FindSftpClient() is { } sftp)
            appData.WinScpPath = sftp;

        // Same idea for the SSH client: show the system's own ssh, which is what gets launched when no path
        // is set. An empty box left the user guessing what "external SSH client" would actually run – and
        // gives them the path to edit if they would rather point it at PuTTY.
        if (appData.ExternalSshClientPath is not { Length: > 0 } && Launchers.FindSystemSsh() is { } ssh)
            appData.ExternalSshClientPath = ssh;

        // The language applies on Save (not on every pick) – deliberate: a half-changed dialog that
        // re-translates under the cursor while you are still deciding is worse than one clean switch.
        if (_languageBox is not null) _languageBox.ItemsSource = Enum.GetValues<AppLanguage>();

        // ⚠️ Populated in code, not bound. The three choices need real sentences ("TikMan's own SSH
        // terminal"), which a bound enum would render as "BuiltIn"; and the selection has to drive the path
        // row's enabled state, which AppData cannot raise because it is not observable.
        if (this.FindControl<ComboBox>("SshClientBox") is { } sshBox)
        {
            sshBox.ItemsSource = new[]
            {
                new SshClientChoice(SshClientKind.BuiltIn, LocalizationManager.T("Av_SshClientBuiltIn")),
                new SshClientChoice(SshClientKind.System, LocalizationManager.T("Av_SshClientSystem")),
                new SshClientChoice(SshClientKind.ThirdParty, LocalizationManager.T("Av_SshClientThirdParty")),
            };
            sshBox.SelectedIndex = appData.SshClient switch
            {
                SshClientKind.BuiltIn => 0,
                SshClientKind.ThirdParty => 2,
                _ => 1,
            };
            UpdateSshPathEnabled(appData.SshClient);
        }
        // The backup-method dropdown is gone (transport is chosen per device now). An older settings file
        // that pinned Web/Ssh is folded back to Auto so it cannot quietly disable a backup.
        if (appData.BackupMethod != BackupMethod.Auto) appData.BackupMethod = BackupMethod.Auto;
        if (this.FindControl<ComboBox>("NotifyLevelBox") is { } notify)
        {
            notify.ItemsSource = new[]
            {
                new NotifyChoice(NotifyLevel.ErrorsOnly, LocalizationManager.T("Av_NotifyErrorsOnly")),
                new NotifyChoice(NotifyLevel.Info, LocalizationManager.T("Av_NotifyAlways")),
            };
            notify.SelectedIndex = appData.NotifyLevel == NotifyLevel.Info ? 1 : 0;
        }
        // (The update-channel picker moved to the Update tab – see the note in the XAML.)
        if (_captureStatus is not null)
            _captureStatus.Text = ZdpScanner.IsAvailable()
                ? LocalizationManager.T("Av_CaptureOk", ZdpScanner.NpcapVersion() ?? "")
                : LocalizationManager.T("Av_CaptureMissing");
        if (this.FindControl<TextBlock>("ConfigPathText") is { } pathText)
            pathText.Text = DeviceStore.StorageDirectory;

        DataContext = appData;
    }

    /// <summary>Applies the picked SSH client to the settings and to the path row's enabled state.
    /// <para>⚠️ Written straight through to <see cref="AppData.SshClient"/>, which is itself a view over the
    /// two stored flags – so the pair can never end up both set.</para></summary>
    private void OnSshClientChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: SshClientChoice choice }) return;
        _appData.SshClient = choice.Kind;
        UpdateSshPathEnabled(choice.Kind);
    }

    private void OnNotifyLevelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: NotifyChoice choice }) _appData.NotifyLevel = choice.Level;
    }

    /// <summary>The path box only means anything for a third-party client: the built-in terminal is inside
    /// TikMan and the system client is found by the OS, so an editable path beside either of those would
    /// invite a setting that changes nothing.</summary>
    private void UpdateSshPathEnabled(SshClientKind kind)
    {
        var on = kind == SshClientKind.ThirdParty;
        if (this.FindControl<TextBlock>("SshPathLabel") is { } label) label.IsEnabled = on;
        if (this.FindControl<TextBox>("SshPathBox") is { } box) box.IsEnabled = on;
        if (this.FindControl<Button>("SshPathPick") is { } pick) pick.IsEnabled = on;
    }

    /// <summary>Fills a path box from a file picker. The button carries the target box's name in its Tag,
    /// so one handler serves all of them.</summary>
    private async void OnPickPath(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string boxName) return;
        if (this.FindControl<TextBox>(boxName) is not { } box) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationManager.T("Av_PickProgram"),
            AllowMultiple = false,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) box.Text = path;
    }

    /// <summary>Opens the IEEE OUI registry page – the source the downloaded list comes from.</summary>
    private void OnOpenOuiSource(object? sender, RoutedEventArgs e)
    {
        try
        {
            var url = "https://standards-oui.ieee.org/";
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", url);
            else System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch { /* no browser – the URL is in the tooltip either way */ }
    }

    private void OnOpenConfigFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = DeviceStore.StorageDirectory;
            Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", dir);
            else System.Diagnostics.Process.Start("xdg-open", dir);
        }
        catch { /* no file manager – the path is shown above either way */ }
    }

    /// <summary>Puts every setting back to its default. ⚠️ Devices and their stored logins are untouched:
    /// this resets preferences, and wiping the inventory from a "reset settings" button would be a nasty
    /// surprise – there is a separate, confirmed action for that.</summary>
    private async void OnResetDefaults(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmWindow.AskAsync(this, LocalizationManager.T("Av_ResetConfirm"),
                LocalizationManager.T("Av_ResetDefaults")))
            return;

        var defaults = new AppData();
        foreach (var name in Fields)
        {
            var prop = typeof(AppData).GetProperty(name);
            if (prop is { CanWrite: true }) prop.SetValue(_appData, prop.GetValue(defaults));
        }
        // Rebind so the controls show the restored values.
        DataContext = null;
        DataContext = _appData;
    }

    /// <summary>Wipes the config folder and restarts, so TikMan comes up as if freshly installed.
    ///
    /// <para>⚠️ The restart is not a courtesy, it is what makes the reset stick: this process still holds the
    /// live <see cref="AppData"/>, and the next save – closing this dialog is already one – would write the
    /// devices and settings straight back over the files just deleted. So nothing is saved from here on, and
    /// the process ends immediately.</para></summary>
    private async void OnFactoryReset(object? sender, RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("FactoryResetStatus");
        if (!await ConfirmWindow.AskAsync(this, LocalizationManager.T("Av_FactoryResetConfirm"),
                LocalizationManager.T("Av_FactoryReset")))
            return;

        try
        {
            DeviceStore.ResetToFactoryState();

            // Relaunch before quitting. Environment.ProcessPath is the real executable in every shape we
            // ship – single-file exe, AppImage and the binary inside the .app bundle.
            if (Environment.ProcessPath is { Length: > 0 } exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                });
        }
        catch (Exception ex)
        {
            if (status is not null)
            {
                status.Text = LocalizationManager.T("Av_FactoryResetFailed") + " " + ex.Message;
                status.IsVisible = true;
            }
            return;
        }

        // Environment.Exit, not Shutdown(): a graceful shutdown runs the save paths we are trying to avoid.
        Environment.Exit(0);
    }

    /// <summary>Sends a mail with the settings currently in the dialog, so they can be checked now instead
    /// of at 03:00 tomorrow.
    ///
    /// <para>⚠️ Uses the password typed into the box if there is one, and the stored one otherwise – the
    /// password field is deliberately not data-bound (an empty box means "unchanged"), so reading only the
    /// saved value would test the old password after the user had just corrected it.</para></summary>
    private async void OnSendTestMail(object? sender, RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("TestMailStatus");
        var button = this.FindControl<Button>("TestMailButton");
        if (status is null) return;

        var typed = _smtpPwBox?.Text ?? "";
        var settings = AutoCheck.SettingsFrom(_appData);
        if (typed.Length > 0) settings = settings with { Password = typed };

        // Validate first: SmtpClient cannot do implicit TLS (465) and would otherwise fail as a timeout
        // minutes later instead of saying what is wrong.
        if (MailSender.Validate(settings) is { Length: > 0 } problem)
        {
            status.Text = problem;
            status.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            return;
        }

        if (button is not null) button.IsEnabled = false;
        status.Foreground = Brushes.Gray;
        status.Text = LocalizationManager.T("Av_TestMailSending");
        try
        {
            await MailSender.SendAsync(settings,
                LocalizationManager.T("Av_TestMailSubject"),
                LocalizationManager.T("Av_TestMailBody", Environment.MachineName));
            status.Text = LocalizationManager.T("Av_TestMailOk",
                string.Join(", ", MailSender.Recipients(_appData.MailTo)));
            status.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        }
        catch (Exception ex)
        {
            // SMTP buries the real reason (auth rejected, host not resolved) an exception or two down.
            var inner = ex; while (inner.InnerException is { } i) inner = i;
            status.Text = LocalizationManager.T("Av_TestMailFailed", inner.Message);
            status.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    private AppUpdater.Available? _pendingUpdate;

    /// <summary>Checks GitHub for a newer TikMan. Two questions, in order: is there a newer version at all
    /// (version-only, always works), and is there an asset this platform can actually install? Only when
    /// both hold – and the layout is swappable – does the "update &amp; restart" button appear.</summary>
    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateStatus is null) return;
        if (_checkUpdateButton is not null) _checkUpdateButton.IsEnabled = false;
        if (_applyUpdateButton is not null) _applyUpdateButton.IsVisible = false;
        _pendingUpdate = null;
        _updateStatus.Text = LocalizationManager.T("Av_Checking");
        try
        {
            // ⚠️ Three components, never Version.ToString() – see TikMan.Core.AppVersion.
            var current = AppVersion.Current(typeof(SettingsWindow).Assembly);
            var (newer, latest, name) = await AppUpdater.CheckVersionAsync(current);
            if (!newer || latest is null)
            {
                _updateStatus.Text = LocalizationManager.T("Av_UpToDate", AppVersion.Text(current));
                return;
            }

            _updateStatus.Text = LocalizationManager.T("Av_NewVersion", AppVersion.Text(latest), name);
            if (!SelfUpdate.IsSupported) return;                       // macOS bundle etc. – report only
            _pendingUpdate = await AppUpdater.CheckPlatformAssetAsync(current);
            if (_pendingUpdate is not null && _applyUpdateButton is not null)
                _applyUpdateButton.IsVisible = true;                   // an installable asset exists
        }
        catch (Exception ex) { _updateStatus.Text = LocalizationManager.T("Av_CheckFailed", ex.Message); }
        finally { if (_checkUpdateButton is not null) _checkUpdateButton.IsEnabled = true; }
    }

    /// <summary>Downloads the platform asset, swaps it in and restarts. The successor deletes the file it
    /// replaced; this process shuts down as soon as the new one is launched.</summary>
    private async void OnApplyUpdate(object? sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _updateStatus is null) return;
        if (_applyUpdateButton is not null) _applyUpdateButton.IsEnabled = false;
        if (_checkUpdateButton is not null) _checkUpdateButton.IsEnabled = false;
        _updateStatus.Text = LocalizationManager.T("Av_Downloading");
        try
        {
            // Modal progress: the app is being replaced, so nothing else should be clickable meanwhile.
            var dlg = new UpdateProgressWindow(_pendingUpdate);
            await dlg.ShowDialog(this);
            if (dlg.Succeeded)
            {
                _updateStatus.Text = LocalizationManager.T("Av_Restarting");
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                return;
            }
            _updateStatus.Text = LocalizationManager.T("Av_UpdateFailed");
        }
        catch (Exception ex) { _updateStatus.Text = LocalizationManager.T("Av_CheckFailed", ex.Message); }
        finally
        {
            if (_applyUpdateButton is not null) _applyUpdateButton.IsEnabled = true;
            if (_checkUpdateButton is not null) _checkUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>Downloads the public IEEE OUI list next to the executable, so MAC-vendor lookups cover every
    /// registered vendor. <see cref="OuiLookup"/> reads the file once per process, so it takes effect on the
    /// next start – the status says so rather than pretending it's live.</summary>
    private async void OnDownloadOui(object? sender, RoutedEventArgs e)
    {
        if (_ouiStatus is null) return;
        if (_ouiButton is not null) _ouiButton.IsEnabled = false;
        _ouiStatus.Text = LocalizationManager.T("Av_OuiDownloading");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var bytes = await http.GetByteArrayAsync("https://standards-oui.ieee.org/oui/oui.txt");
            if (bytes.Length < 100_000) throw new InvalidOperationException("unexpected size " + bytes.Length);
            await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "oui.txt"), bytes);
            _ouiStatus.Text = LocalizationManager.T("Av_OuiDone", (bytes.Length / 1024 / 1024).ToString());
        }
        catch (Exception ex) { _ouiStatus.Text = LocalizationManager.T("Av_OuiFailed", ex.Message); }
        finally { if (_ouiButton is not null) _ouiButton.IsEnabled = true; }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // ⚠️ Only a box the user actually edited replaces its stored password – the SMTP and certificate
        // boxes are pre-filled with a stand-in, so "non-empty" does not mean "newly typed". Clearing an
        // edited box clears the stored password, which is the only way to remove one.
        // The web password is the exception: it is pre-filled in the clear and therefore authoritative from
        // the start (its flag is already true), so an untouched save simply re-writes the same value.
        if (_webPwTouched)
            _appData.WebServerEncryptedPassword = Encrypt(_webPwBox?.Text);
        if (_smtpPwTouched)
            _appData.SmtpEncryptedPassword = Encrypt(_smtpPwBox?.Text);
        if (_certPwTouched)
            _appData.WebServerCertPassword = Encrypt(_certPwBox?.Text);

        // ⚠️ NOT swallowed. This used to be `catch { }`, so a settings write that failed looked exactly like
        // a successful one – the dialog closed, and the setting (a just-typed password, say) was silently
        // gone by the next start. Tell the user instead of letting them find out later.
        try { DeviceStore.Save(_appData); }
        catch (Exception ex)
        {
            if (this.FindControl<TextBlock>("SaveError") is { } err)
            {
                err.Text = LocalizationManager.T("Av_SettingsSaveFailed", ex.Message);
                err.IsVisible = true;
            }
            return; // stay open, so the entered values are not lost along with the write
        }

        LocalizationManager.Instance.Apply(_appData.Language); // switch the whole UI live if the language changed
        Close();
    }

    private static string Encrypt(string? typed) =>
        typed is { Length: > 0 } ? CredentialProtector.Protect(typed) : "";

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        foreach (var f in Fields)
            if (typeof(AppData).GetProperty(f) is { } p && _original.TryGetValue(f, out var v)) p.SetValue(_appData, v);
        Close();
    }
}

/// <summary>One entry in the SSH-client dropdown. <see cref="ToString"/> is what the ComboBox renders, so
/// the item carries its own label instead of needing a template or a converter.</summary>
internal sealed record SshClientChoice(SshClientKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry in the "when to send a mail" dropdown – same idea as <see cref="SshClientChoice"/>.</summary>
internal sealed record NotifyChoice(NotifyLevel Level, string Label)
{
    public override string ToString() => Label;
}
