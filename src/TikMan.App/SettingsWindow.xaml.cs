using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TikMan.App.Localization;
using TikMan.Core.Discovery;
using TikMan.Core.Models;
using TikMan.Core.Storage;

namespace TikMan.App;

/// <summary>Settings: language (takes effect immediately) and full backup method.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppData _data;
    private readonly AppLanguage _originalLanguage;
    private bool _ready;

    /// <summary>Set when the user confirmed a reset; the caller wipes the config and reloads defaults.</summary>
    public bool ResetRequested { get; private set; }

    /// <summary>Set when the user hit "update now" on a found release; the caller performs the swap.</summary>
    public AppUpdater.Available? UpdateRequested { get; private set; }

    private AppUpdater.Available? _foundUpdate;

    public SettingsWindow(AppData data)
    {
        InitializeComponent();
        _data = data;
        _originalLanguage = data.Language;

        LanguageCombo.SelectedValue = data.Language.ToString();
        BackupMethodCombo.SelectedValue = data.BackupMethod.ToString();
        DefaultChannelCombo.SelectedValue = data.DefaultUpdateChannel;
        if (DefaultChannelCombo.SelectedValue is null) DefaultChannelCombo.SelectedIndex = 0; // fall back to stable
        PersistListCheck.IsChecked = data.PersistDeviceList;
        IgnoreCertCheck.IsChecked = data.DefaultIgnoreCertErrors;
        AllowHttpCheck.IsChecked = data.AllowHttpFallback;
        ForceMailtoCheck.IsChecked = data.ForceMailFallback;
        CoffeeButtonCombo.SelectedValue = data.CoffeeButton;
        if (CoffeeButtonCombo.SelectedValue is null) CoffeeButtonCombo.SelectedIndex = 0;
        ExpandRowsCheck.IsChecked = data.ExpandRowsByDefault;
        ContactButtonsCheck.IsChecked = data.ShowContactButtons;
        ListInfoCheck.IsChecked = data.ShowListInfo;
        SingleProgressCheck.IsChecked = data.SingleProgressBar;
        NoInitialScanCheck.IsChecked = data.NoInitialScan;
        CheckUpdatesCheck.IsChecked = data.CheckForUpdates;
        WebAutoStartCheck.IsChecked = data.WebServerAutoStart;
        WebPortBox.Text = data.WebServerPort.ToString();
        WebUserBox.Text = data.WebServerUser;
        WebPasswordBox.Password = CredentialProtector.Unprotect(data.WebServerEncryptedPassword);
        SnmpCommunityBox.Text = data.SnmpCommunity;
        VncNoticeCheck.IsChecked = data.ShowVncNotice;
        PingTimeoutBox.Text = data.PingTimeoutMs.ToString();
        PingRetriesBox.Text = data.PingRetries.ToString();
        ExternalSshCheck.IsChecked = data.UseExternalSshClient;
        SshClientPathBox.Text = data.ExternalSshClientPath;
        WinScpPathBox.Text = data.WinScpPath;
        VlcPathBox.Text = data.VlcPath;
        var npcap = ZdpScanner.NpcapVersion();
        NpcapStatusText.Text = npcap is { Length: > 0 }
            ? LocalizationManager.T("Set_NpcapFound", npcap)
            : LocalizationManager.T("Set_NpcapMissing");
        NpcapStatusText.Foreground = npcap is { Length: > 0 }
            ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.DarkOrange;
        ConfigPathBox.Text = string.Join("\n", DeviceStore.StorageFile,
            Path.Combine(AppContext.BaseDirectory, "oui.txt") + "  (" + LocalizationManager.T("Set_ConfigOui") + ")");
        _ready = true;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (LanguageCombo.SelectedValue is string tag && Enum.TryParse<AppLanguage>(tag, out var lang))
            LocalizationManager.Instance.Apply(lang); // switch immediately (preview)
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* no default browser / blocked – the URL is visible for manual copy */ }
        e.Handled = true;
    }

    private async void OuiDownload_Click(object sender, RoutedEventArgs e)
    {
        OuiDownloadButton.IsEnabled = false;
        OuiStatusText.Text = LocalizationManager.T("Set_OuiDownloading");
        try
        {
            var dest = Path.Combine(AppContext.BaseDirectory, "oui.txt");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            var bytes = await http.GetByteArrayAsync("https://standards-oui.ieee.org/oui/oui.txt");
            await File.WriteAllBytesAsync(dest, bytes);
            OuiStatusText.Text = LocalizationManager.T("Set_OuiDone", dest);
        }
        catch (Exception ex)
        {
            OuiStatusText.Text = LocalizationManager.T("Set_OuiFailed", ex.Message);
        }
        finally
        {
            OuiDownloadButton.IsEnabled = true;
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DeviceStore.StorageDirectory);
            Process.Start(new ProcessStartInfo(DeviceStore.StorageDirectory) { UseShellExecute = true });
        }
        catch { /* nothing to open */ }
    }

    private void SshClientBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "*.exe|*.exe" };
        if (dialog.ShowDialog(this) == true) SshClientPathBox.Text = dialog.FileName;
    }

    private void WinScpBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "WinSCP.exe|WinSCP.exe|*.exe|*.exe" };
        if (dialog.ShowDialog(this) == true) WinScpPathBox.Text = dialog.FileName;
    }

    private void VlcBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "vlc.exe|vlc.exe|*.exe|*.exe" };
        if (dialog.ShowDialog(this) == true) VlcPathBox.Text = dialog.FileName;
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        DoUpdateButton.Visibility = Visibility.Collapsed;
        _foundUpdate = null;
        UpdateStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        UpdateStatusText.Text = LocalizationManager.T("Upd_Checking");
        try
        {
            var (current, exeName) = MainWindow.CurrentBuild();
            var result = await AppUpdater.CheckDetailedAsync(current, exeName);
            switch (result.Outcome)
            {
                case AppUpdater.Outcome.UpToDate:
                    UpdateStatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
                    UpdateStatusText.Text = LocalizationManager.T("Upd_UpToDate", current.ToString());
                    break;
                case AppUpdater.Outcome.UpdateAvailable when result.Update is { } upd:
                    _foundUpdate = upd;
                    UpdateStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
                    UpdateStatusText.Text = LocalizationManager.T("Upd_FoundNamed",
                        upd.Version.ToString(), upd.ReleaseName);
                    DoUpdateButton.Visibility = Visibility.Visible;
                    break;
                default:
                    UpdateStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
                    UpdateStatusText.Text = LocalizationManager.T("Upd_CheckFailed");
                    break;
            }
        }
        catch (Exception)
        {
            UpdateStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
            UpdateStatusText.Text = LocalizationManager.T("Upd_CheckFailed");
        }
        finally { CheckNowButton.IsEnabled = true; }
    }

    private void DoUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_foundUpdate is null) return;
        UpdateRequested = _foundUpdate; // the caller runs the download + swap after we close
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            LocalizationManager.T("Set_ResetConfirm"),
            LocalizationManager.T("Set_Reset"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        ResetRequested = true;
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (LanguageCombo.SelectedValue is string langTag && Enum.TryParse<AppLanguage>(langTag, out var lang))
            _data.Language = lang;
        if (BackupMethodCombo.SelectedValue is string methodTag && Enum.TryParse<BackupMethod>(methodTag, out var method))
            _data.BackupMethod = method;
        if (DefaultChannelCombo.SelectedValue is string channel && channel.Length > 0)
            _data.DefaultUpdateChannel = channel;
        _data.PersistDeviceList = PersistListCheck.IsChecked == true;
        _data.DefaultIgnoreCertErrors = IgnoreCertCheck.IsChecked == true;
        _data.AllowHttpFallback = AllowHttpCheck.IsChecked == true;
        _data.ForceMailFallback = ForceMailtoCheck.IsChecked == true;
        if (CoffeeButtonCombo.SelectedValue is string coffee) _data.CoffeeButton = coffee;
        _data.ExpandRowsByDefault = ExpandRowsCheck.IsChecked == true;
        _data.ShowContactButtons = ContactButtonsCheck.IsChecked == true;
        _data.ShowListInfo = ListInfoCheck.IsChecked == true;
        _data.SingleProgressBar = SingleProgressCheck.IsChecked == true;
        _data.NoInitialScan = NoInitialScanCheck.IsChecked == true;
        _data.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;
        _data.WebServerAutoStart = WebAutoStartCheck.IsChecked == true;
        if (int.TryParse(WebPortBox.Text.Trim(), out var webPort))
            _data.WebServerPort = Math.Clamp(webPort, 1, 65535);
        _data.WebServerUser = WebUserBox.Text.Trim();
        _data.WebServerEncryptedPassword = CredentialProtector.Protect(WebPasswordBox.Password);
        _data.SnmpCommunity = SnmpCommunityBox.Text.Trim() is { Length: > 0 } comm ? comm : "public";
        _data.ShowVncNotice = VncNoticeCheck.IsChecked == true;
        // Ping tuning – clamp to sane bounds so a typo can't hang or disable the scan.
        if (int.TryParse(PingTimeoutBox.Text.Trim(), out var timeout))
            _data.PingTimeoutMs = Math.Clamp(timeout, 100, 5000);
        if (int.TryParse(PingRetriesBox.Text.Trim(), out var retries))
            _data.PingRetries = Math.Clamp(retries, 0, 10);
        _data.UseExternalSshClient = ExternalSshCheck.IsChecked == true;
        _data.ExternalSshClientPath = SshClientPathBox.Text.Trim();
        _data.WinScpPath = WinScpPathBox.Text.Trim();
        _data.VlcPath = VlcPathBox.Text.Trim();

        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // On cancel, revert the live language preview
        if (DialogResult != true)
            LocalizationManager.Instance.Apply(_originalLanguage);
        base.OnClosed(e);
    }
}
