using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TikMan.Core.Storage;

/// <summary>Checks GitHub for a newer TikMan release and downloads the asset that matches the running
/// build's variant (win-x64 / win-arm64, self-contained or framework-dependent). The swap itself – run
/// the new exe, delete the old one – is done by the app, because a running exe can't delete itself.</summary>
public static class AppUpdater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/pgadient/TikMan/releases/latest";

    public sealed record Available(Version Version, string AssetName, string DownloadUrl);

    /// <summary>The newest release on GitHub if it is strictly newer than <paramref name="current"/>
    /// and it ships an asset for this build's variant; null otherwise (including on any network/parse
    /// error – a failed check must never get in the way of starting up).</summary>
    public static async Task<Available?> CheckAsync(Version current, string currentExeName,
        CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TikMan", current.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var json = await http.GetStringAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var latest) || latest <= current) return null;

            var variant = VariantSuffix(currentExeName); // e.g. "win-x64" or "win-arm64-fdd"
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                // Match the exact variant, e.g. "TikMan-1.10.24-win-x64.exe". The "-fdd" must match too:
                // require the suffix immediately before ".exe" so win-x64 never matches win-x64-fdd.
                if (url.Length > 0 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains(variant, StringComparison.OrdinalIgnoreCase) &&
                    IsFdd(name) == IsFdd(currentExeName))
                    return new Available(latest, name, url);
            }
            return null;
        }
        catch (Exception) { return null; } // offline / rate-limited / unexpected JSON – just skip
    }

    /// <summary>Downloads the release asset next to the current executable and returns its full path;
    /// null if the download fails or looks empty/too small to be a real single-file exe.</summary>
    public static async Task<string?> DownloadAsync(Available update, string targetDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var path = Path.Combine(targetDirectory, update.AssetName);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TikMan", update.Version.ToString()));
            using var resp = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(path))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            // A real single-file TikMan build is several MB; a truncated download or an HTML error page
            // would be tiny. Reject anything implausibly small rather than launch a broken file.
            if (new FileInfo(path).Length < 1_000_000) { TryDelete(path); return null; }
            return path;
        }
        catch (Exception)
        {
            TryDelete(path);
            return null;
        }
    }

    /// <summary>The variant suffix of an exe name ("win-x64", "win-arm64", possibly with "-fdd"),
    /// or a best guess from the current process architecture when the file was renamed.</summary>
    private static string VariantSuffix(string exeName)
    {
        var m = Regex.Match(exeName, @"win-(x64|arm64)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Value.ToLowerInvariant();
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
               System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
    }

    private static bool IsFdd(string exeName) => exeName.Contains("-fdd", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseVersion(string tag, out Version version)
    {
        // "v1.10.24" or "1.10.24" → 1.10.24
        var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?");
        if (m.Success && Version.TryParse(m.Value, out var v)) { version = v; return true; }
        version = new Version(0, 0);
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
