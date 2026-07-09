using System.Windows;
using System.Windows.Controls;
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
        DefaultUserBox.Text = data.DefaultUsername;
        _ready = true;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (LanguageCombo.SelectedValue is string tag && Enum.TryParse<AppLanguage>(tag, out var lang))
            LocalizationManager.Instance.Apply(lang); // switch immediately (preview)
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
