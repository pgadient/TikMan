using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using TikMan.Core.Fleet;
using TikMan.Core.Storage;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

/// <summary>The scheduled update check and its e-mail report.
///
/// <para>A <b>check</b>, not an install, and that is the design rather than a step towards one: it only
/// reads, so a run at 03:00 cannot leave a device half-updated or a router rebooting with nobody watching.
/// What it produces is a list of what is available – the deciding and the installing stay with the
/// person.</para>
///
/// <para>⚠️ Until now this existed only in the WPF client, while the Avalonia settings dialog happily
/// stored <c>AutoCheckEnabled</c>, the time and the whole SMTP block. The user could configure a nightly
/// check, TikMan would remember it, and nothing would ever run – a promise the app quietly failed to keep,
/// which is worse than not offering it. This closes that.</para></summary>
public sealed class AutoCheck
{
    private readonly FleetService _fleet;
    private readonly AppData _appData;
    private readonly Action<string> _report;
    private readonly Action _save;

    // One minute is plenty: the slot is a time of day, and a check that starts at 03:00:59 is on time.
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _running;

    public AutoCheck(FleetService fleet, AppData appData, Action<string> report, Action save)
    {
        _fleet = fleet;
        _appData = appData;
        _report = report;
        _save = save;
        _timer.Tick += async (_, _) => await TickAsync();
        Apply();
    }

    /// <summary>Arms or disarms the timer – call after the settings dialog closes.</summary>
    public void Apply() => _timer.IsEnabled = _appData.AutoCheckEnabled;

    public void Stop() => _timer.IsEnabled = false;

    private async Task TickAsync()
    {
        if (_running || !_appData.AutoCheckEnabled || _fleet.Status.Scanning) return;
        if (!IsDueNow(DateTime.Now, _appData.AutoCheckTime, _appData.LastAutoCheck)) return;

        _running = true;
        try
        {
            _appData.LastAutoCheck = DateTime.Now;   // stamp first: a failed run shouldn't retry every minute
            _save();
            await RunAsync();
        }
        catch (Exception ex) { _report(T("Auto_Failed", ex.Message)); }
        finally { _running = false; }
    }

    /// <summary>The due-rule lives in Core (<see cref="AutoCheckSchedule"/>) rather than here: it is the one
    /// part that cannot be checked by using the app, so it belongs somewhere a test can reach it – and a
    /// private copy per client is how two schedules quietly start disagreeing.</summary>
    public static bool IsDueNow(DateTime now, string slot, DateTime? last) =>
        AutoCheckSchedule.IsDueNow(now, slot, last);

    /// <summary>Checks every RouterOS device that has a login and mails the result.</summary>
    private async Task RunAsync()
    {
        // CanUpdate is exactly "RouterOS and has a stored login" – the same pair the WPF client spells out.
        var devices = _fleet.Snapshot().Where(d => d.CanUpdate).ToList();
        if (devices.Count == 0) return;

        _report(T("Auto_Running", devices.Count));
        var available = new List<string>();
        var failed = new List<string>();

        foreach (var d in devices)
        {
            var info = await _fleet.CheckAndRememberAsync(d.Id).ConfigureAwait(true);
            if (info is null) { failed.Add($"{Label(d)}: {T("Av_NoResponse")}"); continue; }
            if (info.UpdateAvailable)
                available.Add($"{Label(d)}: {info.InstalledVersion} → {info.LatestVersion} "
                              + $"[{_fleet.UpdateChannelOf(d.Id)}]");
        }

        _report(T("Auto_Done", available.Count, failed.Count));

        // "Errors only" still means updates: an update waiting is the thing worth being told about, and a
        // mail that only ever arrives when TikMan itself breaks would be a strange definition of news.
        var newsworthy = available.Count > 0 || failed.Count > 0;
        if (_appData.NotifyLevel == NotifyLevel.ErrorsOnly && !newsworthy) return;

        await SendMailAsync(devices.Count, available, failed).ConfigureAwait(true);
    }

    private static string Label(DeviceSnapshot d) =>
        d.Name.Length > 0 ? $"{d.Name} ({d.Ip})" : d.Ip;

    /// <summary>The configured mail account, with the password decrypted only at send time.</summary>
    public static MailSettings SettingsFrom(AppData a) =>
        new(a.SmtpHost, a.SmtpPort, a.SmtpUseTls, a.SmtpUser,
            CredentialProtector.Unprotect(a.SmtpEncryptedPassword), a.MailFrom, a.MailTo);

    private async Task SendMailAsync(int checkedCount, List<string> available, List<string> failed)
    {
        var settings = SettingsFrom(_appData);
        if (MailSender.Validate(settings) is { Length: > 0 } problem)
        {
            _report(T("Auto_MailNotConfigured", problem));   // say it here – it cannot be said by mail
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
            await MailSender.SendAsync(settings, subject, body.ToString()).ConfigureAwait(true);
            _report(T("Auto_MailSent", string.Join(", ", MailSender.Recipients(_appData.MailTo))));
        }
        catch (Exception ex)
        {
            // SMTP wraps the real reason (auth rejected, name not resolved) one or two levels down.
            var inner = ex; while (inner.InnerException is { } i) inner = i;
            _report(T("Auto_MailFailed", inner.Message));
        }
    }
}
