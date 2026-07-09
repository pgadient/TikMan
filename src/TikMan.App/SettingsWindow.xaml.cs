using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TikMan.App.Localization;
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

    public SettingsWindow(AppData data)
    {
        InitializeComponent();
        _data = data;
        _originalLanguage = data.Language;

        LanguageCombo.SelectedValue = data.Language.ToString();
        BackupMethodCombo.SelectedValue = data.BackupMethod.ToString();
        SshPortBox.Text = data.SshPort.ToString();
        DefaultChannelCombo.SelectedValue = data.DefaultUpdateChannel;
        if (DefaultChannelCombo.SelectedValue is null) DefaultChannelCombo.SelectedIndex = 0; // fall back to stable
        PersistListCheck.IsChecked = data.PersistDeviceList;
        IgnoreCertCheck.IsChecked = data.DefaultIgnoreCertErrors;
        DefaultUserBox.Text = data.DefaultUsername;
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
        if (int.TryParse(SshPortBox.Text.Trim(), out var port) && port is >= 1 and <= 65535)
            _data.SshPort = port;
        if (DefaultChannelCombo.SelectedValue is string channel && channel.Length > 0)
            _data.DefaultUpdateChannel = channel;
        _data.PersistDeviceList = PersistListCheck.IsChecked == true;
        _data.DefaultIgnoreCertErrors = IgnoreCertCheck.IsChecked == true;

        _data.DefaultUsername = DefaultUserBox.Text.Trim();
        if (DefaultPasswordBox.Password.Length > 0)
            _data.DefaultEncryptedPassword = CredentialProtector.Protect(DefaultPasswordBox.Password);

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
