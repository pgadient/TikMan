using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TikMan.Core.Storage;

namespace TikMan.Core.Diagnostics;

/// <summary>A short, privacy-scrubbed record of what the user did and what came back, so a bug report can
/// carry something more useful than "it didn't work".
///
/// <para><b>What goes in:</b> actions and outcomes – "scan started", "backup failed: timeout", "update
/// installed". Not data dumps, not device inventories.</para>
///
/// <para><b>What never goes in:</b> passwords and credentials. Not redacted – <i>never passed</i>. A filter
/// is something a caller can forget; a value that is never handed over cannot leak. Every method that deals
/// in secrets logs the fact and not the value ("login stored", never the login).</para>
///
/// <para><b>What gets pseudonymised:</b> addresses, MACs and UNC paths, by
/// <see cref="Redact"/>, which runs on <b>every</b> line at the point of writing rather than at the call
/// site. Callers therefore cannot forget it. Each distinct value keeps a stable token for the life of the
/// process (<c>ip#1</c>, <c>mac#2</c>), so a reader can still follow "ip#3 timed out, then mac#1 answered"
/// – the structure survives, the identity does not.</para>
///
/// <para>Deleting those values outright was the obvious alternative and is worse: for a network tool the
/// topology <i>is</i> the diagnostic information, and a log that says "scan found nothing" without saying
/// how many targets in how many ranges cannot be acted on.</para>
///
/// <para>⚠️ Pseudonymisation is not anonymisation. A log still shows how many devices exist, what kinds
/// they are and which vendors – enough to describe a network, if not to locate it. Hence the reminder in
/// the UI to read it before attaching it to a public issue.</para></summary>
public static class ActionLog
{
    /// <summary>Kept small on purpose: a log a person can actually read before sending it is worth more
    /// than a complete one they won't.</summary>
    private const int MaxLines = 400;

    private static readonly object Gate = new();
    private static readonly Queue<string> Lines = new();
    private static readonly ConcurrentDictionary<string, string> Tokens = new();
    private static int _nextIp, _nextMac, _nextHost;

    /// <summary>Turned off by <see cref="AppData.DisableActionLog"/>. Nothing is buffered when off.</summary>
    public static bool Enabled { get; set; } = true;

    public static string LogFile => Path.Combine(DeviceStore.StorageDirectory, "tikman-actions.log");

    /// <summary>Records one action. <paramref name="detail"/> is optional context – it is redacted along
    /// with everything else, so passing an address by accident is harmless.</summary>
    public static void Write(string action, string? detail = null)
    {
        if (!Enabled) return;
        try
        {
            var line = detail is { Length: > 0 }
                ? $"{DateTime.Now:HH:mm:ss}  {action}  {Redact(detail)}"
                : $"{DateTime.Now:HH:mm:ss}  {action}";

            lock (Gate)
            {
                Lines.Enqueue(line);
                while (Lines.Count > MaxLines) Lines.Dequeue();
            }
        }
        catch { /* diagnostics must never be the thing that breaks */ }
    }

    /// <summary>The buffered log, oldest first, with a header naming the build and platform – the two
    /// things every bug report needs and nobody remembers to include.</summary>
    public static string Snapshot()
    {
        lock (Gate)
        {
            var header = new[]
            {
                $"TikMan {AppVersion.Text()}",
                $"OS: {Environment.OSVersion} ({(Environment.Is64BitProcess ? "64" : "32")}-bit)",
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm}",
                "",
                "Addresses and MACs are replaced with per-session tokens (ip#1, mac#2). Passwords are",
                "never recorded. Please still read this through before attaching it to a public report.",
                "",
            };
            return string.Join(Environment.NewLine, header.Concat(Lines));
        }
    }

    /// <summary>Writes the log next to the settings, for attaching to a report. Returns the path, or ""
    /// when it could not be written.</summary>
    public static string SaveToFile()
    {
        try
        {
            Directory.CreateDirectory(DeviceStore.StorageDirectory);
            File.WriteAllText(LogFile, Snapshot());
            return LogFile;
        }
        catch { return ""; }
    }

    public static void Clear()
    {
        lock (Gate) Lines.Clear();
        Tokens.Clear();
    }

    // ---- redaction ---------------------------------------------------------------------------------

    // A MAC first: it would otherwise be partly eaten by the IPv6 pattern's colons.
    private static readonly Regex MacPattern =
        new(@"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);

    private static readonly Regex Ipv4Pattern =
        new(@"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.Compiled);

    // Deliberately loose: anything with two or more colons and hex groups. Over-matching here costs a
    // token where none was needed; under-matching leaks an address.
    private static readonly Regex Ipv6Pattern =
        new(@"\b(?=[0-9A-Fa-f:]*:)[0-9A-Fa-f]{0,4}(:[0-9A-Fa-f]{0,4}){2,7}\b", RegexOptions.Compiled);

    private static readonly Regex UncPattern =
        new(@"\\\\[^\s\\]+", RegexOptions.Compiled);

    /// <summary>Replaces identifying values with stable per-session tokens. Public so its behaviour can be
    /// pinned by tests – this is the one function standing between a bug report and someone's network.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        text = MacPattern.Replace(text, m => Token("mac", m.Value.ToUpperInvariant(), ref _nextMac));
        text = Ipv4Pattern.Replace(text, m => Token("ip", m.Value, ref _nextIp));
        text = Ipv6Pattern.Replace(text, m => Token("ip", m.Value.ToUpperInvariant(), ref _nextIp));
        text = UncPattern.Replace(text, m => Token("host", m.Value.ToUpperInvariant(), ref _nextHost));
        return text;
    }

    /// <summary>Same value in, same token out – for the life of the process. The mapping is never written
    /// anywhere, so the tokens mean nothing once the app closes.</summary>
    /// <summary>A second, stronger pass for a log that is about to be posted somewhere public: replaces the
    /// names TikMan already knows – device names, this machine, the signed-in user – with stable tokens.
    ///
    /// <para>⚠️ Names are the part <see cref="Redact"/> deliberately leaves alone, because for reading your
    /// own log locally "GW-Office timed out" is far more use than "name#4". On a public issue the same line
    /// is a piece of someone's infrastructure, often with a company or a person in it. Two destinations,
    /// two levels – and the caller says which, rather than one compromise that serves neither.</para>
    ///
    /// <para>⚠️ Driven by the caller's list of known names, not by pattern matching. Guessing which words in
    /// a log line are names would both miss real ones and mangle ordinary text; an exact list cannot. Names
    /// are replaced longest-first so "GW" inside "GW-Office" cannot shadow the longer match.</para></summary>
    public static string RedactNames(string text, IEnumerable<string> names)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var ordered = names
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3)   // "PC" would hit prose
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();

        foreach (var name in ordered)
            text = Regex.Replace(text, Regex.Escape(name),
                _ => Token("name", name.ToUpperInvariant(), ref _nextName),
                RegexOptions.IgnoreCase);

        return text;
    }

    private static int _nextName;

    private static string Token(string kind, string value, ref int counter)
    {
        var key = kind + ":" + value;
        if (Tokens.TryGetValue(key, out var existing)) return existing;

        var assigned = $"{kind}#{System.Threading.Interlocked.Increment(ref counter)}";
        return Tokens.GetOrAdd(key, assigned);
    }
}
