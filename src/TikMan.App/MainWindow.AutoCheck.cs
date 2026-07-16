using System.Text;
using System.Windows.Threading;
using TikMan.Core.Storage;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>The scheduled update check and its e-mail.
/// <para>A <b>check</b>, not an install, and that is the design rather than a first step towards one:
/// it only reads, so a run at 03:00 cannot leave a device half-updated or a router rebooting with
/// nobody watching. What it produces is a list of what's available — the deciding and the installing
/// stay with the person.</para></summary>
public partial class MainWindow
{
    // One minute is plenty: the slot is a time of day, and a check that starts at 03:00:59 is on time.
    private readonly DispatcherTimer _autoCheckTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _autoCheckRunning;

    private void InitAutoCheck()
    {
        _autoCheckTimer.Tick += async (_, _) => await AutoCheckTickAsync();
        ApplyAutoCheckSettings();
    }

    /// <summary>Arms or disarms the timer after the settings change.</summary>
    private void ApplyAutoCheckSettings() => _autoCheckTimer.IsEnabled = _appData.AutoCheckEnabled;

    /// <summary>Fires when today's slot has passed and we haven't run for it yet.
    /// <para>"Has passed and wasn't run" rather than "is now": TikMan is a desktop app, so 03:00 is a
    /// time nobody may be around for. A slot missed because the app was closed is caught up the next
    /// time it's open, which is the difference between a schedule that mostly works and one that only
    /// works on machines that never sleep.</para></summary>
    private async Task AutoCheckTickAsync()
    {
        if (_autoCheckRunning || !_appData.AutoCheckEnabled || _scanning) return;
        if (!IsDueNow(DateTime.Now, _appData.AutoCheckTime, _appData.LastAutoCheck)) return;

        _autoCheckRunning = true;
        try
        {
            _appData.LastAutoCheck = DateTime.Now;   // stamp first: a failed run shouldn't retry every minute
            SaveAppData();
            await RunAutoCheckAsync();
        }
        catch (Exception ex) { SetStatus(T("Auto_Failed", ex.Message)); }
        finally { _autoCheckRunning = false; }
    }

    /// <summary>Pure, so the awkward part is testable: is <paramref name="now"/> past today's slot, with
    /// no run recorded for it yet? <paramref name="last"/> null means "never ran" – which is due.</summary>
    public static bool IsDueNow(DateTime now, string slot, DateTime? last)
    {
        if (!TimeSpan.TryParseExact(slot, new[] { @"h\:mm", @"hh\:mm" },
                System.Globalization.CultureInfo.InvariantCulture, out var time)) return false;
        var todaysSlot = now.Date + time;
        if (now < todaysSlot) return false;          // not yet
        return last is null || last < todaysSlot;    // not run for this slot
    }

    /// <summary>Checks every RouterOS device that has a login and mails the result.</summary>
    private async Task RunAutoCheckAsync()
    {
        var devices = _devices.Where(d => d.HasCredentials && d.IdentifiedVendor == "MikroTik").ToList();
        if (devices.Count == 0) return;

        SetStatus(T("Auto_Running", devices.Count));
        var available = new List<string>();
        var failed = new List<string>();

        foreach (var d in devices)
        {
            if (await d.CheckUpdateAsync())
            {
                if (d.UpdateAvailable)
                    available.Add($"{d.Name} ({d.Host}): {d.Version} → {d.LatestVersion} [{d.UpdateChannel}]");
            }
            else failed.Add($"{d.Name} ({d.Host}): {d.LastError}");
        }

        SetStatus(T("Auto_Done", available.Count, failed.Count));

        // "Errors only" still means updates: an update waiting is the thing worth being told about, and
        // a mail that only ever arrives when TikMan itself breaks would be a strange definition of news.
        var newsworthy = available.Count > 0 || failed.Count > 0;
        if (_appData.NotifyLevel == NotifyLevel.ErrorsOnly && !newsworthy) return;

        await SendAutoCheckMailAsync(devices.Count, available, failed);
    }

    private async Task SendAutoCheckMailAsync(int checkedCount, List<string> available, List<string> failed)
    {
        var settings = new MailSettings(_appData.SmtpHost, _appData.SmtpPort, _appData.SmtpUseTls,
            _appData.SmtpUser, CredentialProtector.Unprotect(_appData.SmtpEncryptedPassword),
            _appData.MailFrom, _appData.MailTo);
        if (MailSender.Validate(settings) is { Length: > 0 } problem)
        {
            SetStatus(T("Auto_MailNotConfigured", problem));   // say it here – it can't be said by mail
            return;
        }

        var subject = failed.Count > 0 ? T("Mail_SubjectProblems", failed.Count)
                    : available.Count > 0 ? T("Mail_SubjectUpdates", available.Count)
                    : T("Mail_SubjectNothing");

        var body = new StringBuilder();
        body.AppendLine(T("Mail_Intro", Environment.MachineName, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        body.AppendLine();
        body.AppendLine(T("Mail_Checked", checkedCount));
        body.AppendLine();
        if (available.Count > 0)
        {
            body.AppendLine(T("Mail_UpdatesHeader"));
            foreach (var line in available) body.AppendLine("  • " + line);
            body.AppendLine();
        }
        if (failed.Count > 0)
        {
            body.AppendLine(T("Mail_FailedHeader"));
            foreach (var line in failed) body.AppendLine("  • " + line);
            body.AppendLine();
        }
        if (available.Count == 0 && failed.Count == 0) { body.AppendLine(T("Mail_AllCurrent")); body.AppendLine(); }
        body.AppendLine(T("Mail_Footer"));

        try
        {
            await MailSender.SendAsync(settings, subject, body.ToString());
            SetStatus(T("Auto_MailSent", string.Join(", ", MailSender.Recipients(_appData.MailTo))));
        }
        catch (Exception ex)
        {
            var inner = ex; while (inner.InnerException is { } i) inner = i;
            SetStatus(T("Auto_MailFailed", inner.Message));
        }
    }
}
