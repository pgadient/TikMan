using System.Net.Http;
using System.Text.Json;

namespace TikMan.Core.Api;

/// <summary>The latest released firmware for a UniFi device, read from Ubiquiti's public firmware API.
///
/// <para>Unlike the HTML-scraping <see cref="FirmwareLatest"/>, UniFi exposes a clean JSON endpoint
/// (<c>fw-update.ubnt.com/api/firmware-latest</c>) keyed by the board's <b>platform</b> code — which is exactly
/// the device's <c>board.shortname</c> (e.g. "USL16LPB"), read over SSH. So there is no fragile model→code
/// table: the device tells us its own key.</para>
///
/// <para>⚠️ The response is UNTRUSTED external data. This reader only ever DESERIALISES it and pulls three
/// numeric version parts and a date string out — nothing in the payload is executed, followed, or used to build
/// a request. The platform code is validated to be alphanumeric before it goes into the query, so a crafted
/// value can't inject into the URL. Any error (offline, blocked, shape change) fails soft to null.</para></summary>
public static class UbiquitiFirmware
{
    /// <summary>Latest firmware version (e.g. "7.4.1") and its release date (e.g. "2026-04-07").</summary>
    public readonly record struct LatestInfo(string Version, string ReleaseDate);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return c;
    }

    /// <summary>The latest release-channel firmware for a UniFi <paramref name="platformCode"/> (the board
    /// short-name), or null when the code is unusable, the API can't be reached, or nothing matched.</summary>
    public static async Task<LatestInfo?> QueryLatestAsync(string platformCode, CancellationToken ct = default)
    {
        platformCode = (platformCode ?? "").Trim();
        // Guard the code before it becomes part of the URL: alphanumeric only (real codes look like "USL16LPB").
        if (platformCode.Length is 0 or > 24 || !platformCode.All(char.IsLetterOrDigit)) return null;

        var url = "https://fw-update.ubnt.com/api/firmware-latest" +
                  "?filter=eq~~channel~~release&filter=eq~~platform~~" + platformCode;

        string json;
        try { json = await Http.GetStringAsync(url, ct).ConfigureAwait(false); }
        catch { return null; }   // offline / blocked ⇒ no version, caller falls back to the plain page link

        return Parse(json, platformCode);
    }

    /// <summary>Parses the firmware-latest JSON. Public and pure so it can be pinned against a captured payload
    /// without a network round trip. Picks the entry whose platform matches (else the first), and reads its
    /// <c>version_major.minor.patch</c> and <c>created</c> date. Null on any shape it doesn't recognise.</summary>
    public static LatestInfo? Parse(string? json, string platformCode)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("_embedded", out var emb) ||
                !emb.TryGetProperty("firmware", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? chosen = null;
            foreach (var fw in arr.EnumerateArray())
            {
                chosen ??= fw;   // fall back to the first entry
                if (fw.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String &&
                    string.Equals(p.GetString(), platformCode, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = fw;
                    break;
                }
            }
            if (chosen is not { } el) return null;

            var version = BuildVersion(el);
            if (version.Length == 0) return null;
            var date = BuildDate(el);
            return new LatestInfo(version, date);
        }
        catch { return null; }   // malformed / hostile JSON ⇒ soft fail
    }

    private static string BuildVersion(JsonElement el)
    {
        // Prefer the structured integer parts; they are unambiguous ("v7.4.1+16850" strings vary in shape).
        if (el.TryGetProperty("version_major", out var maj) && maj.TryGetInt32(out var vmaj) &&
            el.TryGetProperty("version_minor", out var min) && min.TryGetInt32(out var vmin) &&
            el.TryGetProperty("version_patch", out var pat) && pat.TryGetInt32(out var vpat) &&
            vmaj is >= 0 and < 1000 && vmin is >= 0 and < 1000 && vpat is >= 0 and < 10000)
            return $"{vmaj}.{vmin}.{vpat}";
        return "";
    }

    private static string BuildDate(JsonElement el)
    {
        // "created" / "updated" are ISO-8601 (e.g. "2026-04-07T07:15:59Z"); keep just the calendar day.
        foreach (var key in new[] { "created", "updated" })
            if (el.TryGetProperty(key, out var d) && d.ValueKind == JsonValueKind.String &&
                d.GetString() is { Length: >= 10 } s &&
                s[4] == '-' && s[7] == '-')
                return s[..10];
        return "";
    }
}
