using System.Net;
using System.Net.Mail;

namespace TikMan.Core.Storage;

/// <summary>Where a notification goes and how it gets there. Passed in rather than read from AppData so
/// this stays a pure Core concern – and so the password arrives already decrypted, for the send and
/// nothing else.</summary>
public sealed record MailSettings(string Host, int Port, bool UseTls, string User, string Password,
    string From, string To);

/// <summary>Sends TikMan's notification e-mails.
/// <para>Built on <see cref="SmtpClient"/>, which is enough for STARTTLS submission (port 587) – the
/// normal case. ⚠️ It cannot do <b>implicit</b> TLS (port 465): it always connects in the clear and
/// upgrades, so a 465 server just hangs up. That would need MailKit, i.e. a dependency; until someone
/// actually needs 465, <see cref="Validate"/> says so instead of failing at send time with a timeout
/// nobody can read.</para></summary>
public static class MailSender
{
    /// <summary>Why these settings can't send, or "" when they look sendable. Checked before the
    /// schedule is armed, so a typo surfaces while the user is looking at the settings rather than at
    /// 03:00 in a mail that never arrives.</summary>
    public static string Validate(MailSettings m)
    {
        if (m.Host.Trim().Length == 0) return "no SMTP server";
        if (m.Port is <= 0 or > 65535) return "invalid port";
        if (m.From.Trim().Length == 0) return "no sender address";
        if (Recipients(m.To).Count == 0) return "no recipients";
        if (m.Port == 465) return "port 465 (implicit TLS) is not supported – use 587 (STARTTLS)";
        return "";
    }

    /// <summary>Splits the comma-separated recipient list. Blanks and stray spaces fall out, so
    /// "a@b.ch, , c@d.ch," is two recipients rather than an error.</summary>
    public static List<string> Recipients(string list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();

    /// <summary>Sends one mail. Throws on failure – the caller decides whether that is worth reporting,
    /// and to whom (it can hardly be reported by e-mail).</summary>
    public static async Task SendAsync(MailSettings m, string subject, string body,
        CancellationToken ct = default)
    {
        var problem = Validate(m);
        if (problem.Length > 0) throw new InvalidOperationException(problem);

        using var client = new SmtpClient(m.Host.Trim(), m.Port)
        {
            EnableSsl = m.UseTls,
            Timeout = 30_000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        // An empty user means an unauthenticated relay – common for an internal mail server, and the
        // reason this isn't unconditional: sending empty credentials makes some servers refuse outright.
        if (m.User.Trim().Length > 0)
        {
            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(m.User.Trim(), m.Password);
        }

        using var mail = new MailMessage { From = new MailAddress(m.From.Trim()), Subject = subject, Body = body };
        foreach (var to in Recipients(m.To)) mail.To.Add(to);

        await client.SendMailAsync(mail, ct).ConfigureAwait(false);
    }
}
