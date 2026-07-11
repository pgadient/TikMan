using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>Asks a Swisscom Internet-Box for its exact model ("Internet-Box 5 Pro" etc.), serial
/// number and firmware. First over the SoftAtHome JSON-RPC endpoint (<c>POST /ws</c>,
/// DeviceInfo.get – these boxes answer it without a login), then, as fallback, by scanning the
/// login page HTML for the model string. Read-only and best-effort.</summary>
public static partial class SwisscomProbe
{
    public readonly record struct BoxInfo(string ModelName, string Serial, string Firmware);

    // "Internet-Box 5 Pro", "Internet Box 3", "InternetBox4", … – the suffix is optional.
    [GeneratedRegex(@"internet[\s_-]?box[\s ]*((?:\d+\w*|one|go)?(?:[\s ]+(?:pro|plus|standard|ultra))?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ModelRegex();

    public static async Task<BoxInfo?> QueryAsync(string host, CancellationToken ct = default)
    {
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TikMan/1.0");

        foreach (var scheme in new[] { "http", "https" })
        {
            // 1) SoftAtHome JSON-RPC: DeviceInfo.get is readable pre-login on these boxes.
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{scheme}://{hostPart}/ws")
                {
                    Content = new StringContent(
                        """{"service":"DeviceInfo","method":"get","parameters":{}}""",
                        Encoding.UTF8, "application/x-sah-ws-4-call+json"),
                };
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    var model = FirstOf(doc.RootElement, "ProductClass", "ModelName", "FriendlyName");
                    var serial = FirstOf(doc.RootElement, "SerialNumber");
                    var firmware = FirstOf(doc.RootElement, "SoftwareVersion");
                    if (model.Length > 0 || serial.Length > 0)
                        return new BoxInfo(Canonical(model), serial, firmware);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                // endpoint absent on this scheme – fall through
            }
        }

        // 2) Fallback: the login page usually names the model somewhere in HTML/JS.
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                var html = await http.GetStringAsync($"{scheme}://{hostPart}/", ct).ConfigureAwait(false);
                string best = "";
                foreach (Match m in ModelRegex().Matches(html))
                {
                    var candidate = Canonical(m.Value);
                    if (candidate.Length > best.Length) best = candidate; // most specific wins
                }
                if (best.Length > 0) return new BoxInfo(best, "", "");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // unreachable on this scheme
            }
        }
        return null;
    }

    /// <summary>Normalises any spelling to "Internet-Box[ suffix]" with tidy casing ("5 Pro").</summary>
    private static string Canonical(string raw)
    {
        if (raw.Length == 0) return "";
        var m = ModelRegex().Match(raw);
        if (!m.Success) return raw.Trim(); // /ws returned something else entirely – keep it
        var suffix = Regex.Replace(m.Groups[1].Value, @"[\s ]+", " ").Trim();
        if (suffix.Length == 0) return "Internet-Box";
        suffix = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(suffix.ToLowerInvariant());
        return $"Internet-Box {suffix}";
    }

    /// <summary>Depth-first search for the first non-empty string value under any of the keys.</summary>
    private static string FirstOf(JsonElement e, params string[] keys)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (keys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) &&
                        p.Value.ValueKind == JsonValueKind.String &&
                        p.Value.GetString() is { Length: > 0 } s)
                        return s;
                foreach (var p in e.EnumerateObject())
                    if (FirstOf(p.Value, keys) is { Length: > 0 } nested)
                        return nested;
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (FirstOf(item, keys) is { Length: > 0 } nested)
                        return nested;
                break;
        }
        return "";
    }
}
