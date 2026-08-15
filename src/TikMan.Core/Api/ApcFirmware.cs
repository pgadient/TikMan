using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TikMan.Core.Api;

/// <summary>The latest published firmware for an APC / Schneider Network Management Card, read from Schneider's
/// download API.
///
/// <para>⚠️ WHY <c>curl</c> AND NOT <see cref="System.Net.Http.HttpClient"/>: the API sits behind Akamai Bot
/// Manager, which fingerprints the TLS ClientHello (JA3). Measured on a real machine: <c>curl.exe</c> (Windows
/// Schannel) is let through, but EVERY .NET path is blocked with 403 – SocketsHttpHandler and WinHttpHandler,
/// HTTP/1.1 and HTTP/2, with browser headers, with the Expect header off. Only the OS <c>curl</c> passes. So
/// this reader shells out to it. Windows is solid; on Linux/macOS the system curl uses OpenSSL/SecureTransport,
/// a different fingerprint Akamai may still block – so this fails SOFT everywhere and the caller falls back to
/// the plain "open the firmware page" link, exactly like an EOL Zyxel.</para>
///
/// <para>⚠️ The API is called with a FIXED url and a FIXED body ("{}"); no device data goes into the request,
/// so there is nothing to inject. The JSON reply is UNTRUSTED – <see cref="Parse"/> only reads a version and a
/// date out of it and never treats any field as anything but data.</para></summary>
public static class ApcFirmware
{
    /// <summary>Latest firmware version (e.g. "2.5.5.1") and its release date (e.g. "2025-06-13").</summary>
    public readonly record struct LatestInfo(string Version, string ReleaseDate);

    // Product range 61936 = "Network Management Cards" – every AP963x/AP964x NMC firmware lives here. One POST
    // returns them all; we then pick the one whose file matches the device's own application firmware.
    private const string ApiUrl =
        "https://www.se.com/us/en/download/download-api/range/61936/" +
        "?appSource=PES_SE&view=smallcard&fetchSoftwares=only&pageNumber=1&itemsPerPage=100";

    // ⚠️ The FULL browser header set is required, measured: with minimal headers Akamai returns 403; with these
    // (a real UA, a multi-language Accept-Language, Origin/Referer on the site, and the Sec-Fetch-* set) the
    // system curl gets 200 reliably (5/5 on Windows Schannel, curl 7.64 AND 8.21). Dropping any of them risks
    // the 403 again. (Accept-Encoding is deliberately NOT set – letting curl pick via --compressed; forcing
    // "br" made an older curl fail with 400.)
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:155.0) Gecko/20100101 Firefox/155.0";

    private const string PageReferer = FirmwarePages.ApcNmcPage;

    /// <summary>The latest firmware for the given application code (the base of the device's own firmware file,
    /// e.g. "apc_hw21_su" from <c>apc_hw21_su_2.5.5.1.bin</c>), or null when curl is unavailable, the API can't
    /// be reached (Akamai 403 on this platform), or nothing matched.</summary>
    public static async Task<LatestInfo?> QueryLatestAsync(string appCode, CancellationToken ct = default)
    {
        appCode = (appCode ?? "").Trim();
        if (appCode.Length == 0) return null;

        var json = await CurlPostAsync(ct).ConfigureAwait(false);
        return json is null ? null : Parse(json, appCode);
    }

    /// <summary>POSTs the fixed query via the OS <c>curl</c> and returns its stdout, or null on any failure
    /// (curl missing, non-zero exit, timeout, empty output). Never throws.</summary>
    private static async Task<string?> CurlPostAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("curl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // ArgumentList (not a joined string) so nothing is shell-parsed. All values are constants.
            foreach (var a in new[]
            {
                "-s", "-X", "POST", "--max-time", "20", "--compressed",
                "-H", "User-Agent: " + UserAgent,
                "-H", "Accept: application/json, text/plain, */*",
                "-H", "Accept-Language: en-US,en;q=0.9,de-CH;q=0.8,de-DE;q=0.7",
                "-H", "Content-Type: application/json",
                "-H", "Origin: https://www.se.com",
                "-H", "Referer: " + PageReferer,
                "-H", "Sec-Fetch-Dest: empty",
                "-H", "Sec-Fetch-Mode: cors",
                "-H", "Sec-Fetch-Site: same-origin",
                "--data", "{}", ApiUrl,
            }) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try { await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return null; }

            if (proc.ExitCode != 0) return null;
            var text = await stdout.ConfigureAwait(false);
            // Akamai's block page is HTML; a real answer is JSON. Cheap guard before parsing.
            return text.TrimStart().StartsWith("{", StringComparison.Ordinal) ? text : null;
        }
        catch { return null; }   // curl not on PATH, or any process error
    }

    /// <summary>Parses the download-API JSON and returns the version + date of the firmware document whose file
    /// matches <paramref name="appCode"/>. Public and pure so the smoke test can pin it against a captured
    /// payload. Null when the shape isn't recognised or no file matched.</summary>
    public static LatestInfo? Parse(string? json, string appCode)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(appCode)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("documents", out var docs) || docs.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var d in docs.EnumerateArray())
            {
                if (!DocumentMatches(d, appCode)) continue;
                var version = d.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (version.Length == 0) continue;
                var date = d.TryGetProperty("documentDate", out var dt) && dt.ValueKind == JsonValueKind.String
                    ? NormalizeDate(dt.GetString() ?? "") : "";
                return new LatestInfo(CleanVersion(version), date);
            }
            return null;
        }
        catch { return null; }
    }

    // A document matches when one of its files' base name equals the device's application code. The base is the
    // file name up to the version ("apc_hw21_su_2-5-5-1.exe" → "apc_hw21_su"); the version separator differs
    // between the device ('.') and the API ('-'), which is exactly why we compare the BASE, not the version.
    private static bool DocumentMatches(JsonElement doc, string appCode)
    {
        if (doc.TryGetProperty("documentFiles", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var f in files.EnumerateArray())
                if (f.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
                    && string.Equals(FileBase(fn.GetString()), appCode, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    /// <summary>The firmware file base: everything before the <c>_&lt;digit&gt;</c> version tail. Public + pure
    /// so it can be pinned. "apc_hw21_su_2-5-5-1.exe" → "apc_hw21_su"; "apc_hw21_su_2.5.5.1.bin" → "apc_hw21_su".</summary>
    public static string FileBase(string? filename)
    {
        var name = (filename ?? "").Trim();
        var m = Regex.Match(name, @"^(.*?)_\d");   // stop at the first "_<digit>"
        return (m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(name)).ToLowerInvariant();
    }

    // "v2.5.5.1" / "2.5.5.1" → "2.5.5.1"; leaves anything else as-is.
    private static string CleanVersion(string v) => v.TrimStart('v', 'V').Trim();

    // "Jun/13/2025" → "2025-06-13"; "" when it isn't that shape.
    private static string NormalizeDate(string raw) =>
        DateTime.TryParseExact(raw.Trim(), "MMM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
}
