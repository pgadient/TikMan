using System.Collections.Concurrent;
using System.Globalization;

namespace TikMan.Core.Api;

/// <summary>Fetches the release date of a specific RouterOS version from its per-version
/// changelog on the upgrade server, e.g.
/// https://upgrade.mikrotik.com/routeros/7.23.2/CHANGELOG whose first line reads
/// «What's new in 7.23.2 (2026-Jul-03 12:08):». Results are cached per version, so a whole
/// fleet on the same version only triggers a single request.</summary>
public static class ChangelogClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, DateTime?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DateFormats = { "yyyy-MMM-dd HH:mm", "yyyy-MMM-dd" };

    /// <summary>Release date of the given bare version (e.g. "7.23.2"), or null if unavailable.</summary>
    public static async Task<DateTime?> GetReleaseDateAsync(string version, CancellationToken ct = default)
    {
        version = version.Trim();
        if (version.Length == 0) return null;
        if (Cache.TryGetValue(version, out var cached)) return cached;

        DateTime? date = null;
        try
        {
            var text = await Http.GetStringAsync(
                $"https://upgrade.mikrotik.com/routeros/{Uri.EscapeDataString(version)}/CHANGELOG", ct)
                .ConfigureAwait(false);
            date = ParseFirstDate(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // offline / unknown version – cache the null so we don't retry every poll
        }

        Cache[version] = date;
        return date;
    }

    /// <summary>Extracts the date from «… (2026-Jul-03 12:08):» on the first line.</summary>
    private static DateTime? ParseFirstDate(string text)
    {
        int open = text.IndexOf('(');
        int close = open >= 0 ? text.IndexOf(')', open + 1) : -1;
        if (open < 0 || close < 0) return null;

        var inner = text[(open + 1)..close].Trim();
        return DateTime.TryParseExact(inner, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }
}
