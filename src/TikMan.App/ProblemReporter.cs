using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using TikMan.Core.Storage;

namespace TikMan.App;

/// <summary>Builds a diagnostic report and opens a pre-filled problem e-mail. Prefers Outlook
/// Classic (COM automation) so the log can be attached as a file; falls back to the default mail
/// handler via a mailto link (Outlook New, etc.), where the log is put inline in the body.</summary>
[SupportedOSPlatform("windows")]
public static class ProblemReporter
{
    public const string ToAddress = "debug@pcproblem.ch";

    /// <summary>Writes a diagnostic log (version, OS, crash.log) to a temp file and returns the
    /// file path plus the same text (for inlining in the mailto fallback).</summary>
    public static (string FilePath, string Text) BuildReport(string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TikMan {version}");
        sb.AppendLine($"OS:   {RuntimeInformation.OSDescription}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        var crash = Path.Combine(DeviceStore.StorageDirectory, "crash.log");
        if (File.Exists(crash))
        {
            sb.AppendLine("=== crash.log ===");
            try { sb.AppendLine(File.ReadAllText(crash)); } catch (IOException) { sb.AppendLine("(crash.log unreadable)"); }
        }
        else
        {
            sb.AppendLine("(no crash.log — describe the problem in the e-mail)");
        }

        var text = sb.ToString();
        var path = "";
        try
        {
            path = Path.Combine(Path.GetTempPath(), $"TikMan-report-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, text);
        }
        catch (IOException) { path = ""; }
        return (path, text);
    }

    /// <summary>Creates a mail in Outlook Classic (COM) with the log attached and shows it. Returns
    /// false when Outlook Classic isn't available / automation failed.</summary>
    public static bool TryOutlookClassic(string subject, string body, string attachmentPath)
    {
        object? app = null;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null) return false;
            app = Activator.CreateInstance(type);
            if (app is null) return false;

            dynamic outlook = app;
            dynamic mail = outlook.CreateItem(0); // olMailItem
            mail.To = ToAddress;
            mail.Subject = subject;
            mail.Body = body;
            if (attachmentPath.Length > 0 && File.Exists(attachmentPath))
                mail.Attachments.Add(attachmentPath);
            mail.Display(false); // show the draft for the user to review and send
            return true;
        }
        catch (COMException) { return false; }
        catch (Exception ex) when (ex is InvalidCastException or MissingMemberException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Opens the default mail handler (Outlook New, …) with a pre-filled mailto. Files can't
    /// be attached this way, so the caller passes the log inline in the body.</summary>
    public static bool TryMailto(string subject, string body)
    {
        try
        {
            var url = $"mailto:{ToAddress}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
    }
}
