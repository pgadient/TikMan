using System.Net;
using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>Reads the unauthenticated Brother "Maintenance Information" page
/// (<c>/general/information.html?kind=item</c>, older EWS firmwares) and extracts the serial
/// number plus the main/sub firmware versions. Strictly best-effort and read-only.</summary>
public static partial class BrotherProbe
{
    public readonly record struct BrotherInfo(
        string Serial,
        string MainFirmware,
        IReadOnlyList<KeyValuePair<string, string>> SubFirmware);

    [GeneratedRegex(@"<dt[^>]*>(.*?)</dt>\s*<dd[^>]*>(.*?)</dd>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PairRegex();

    public static async Task<BrotherInfo?> QueryAsync(string host, CancellationToken ct = default)
    {
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        foreach (var scheme in new[] { "http", "https" }) // older Brothers serve this over plain http
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    AllowAutoRedirect = true,
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TikMan/1.0");
                var html = await http.GetStringAsync(
                    $"{scheme}://{hostPart}/general/information.html?kind=item", ct).ConfigureAwait(false);

                string serial = "", main = "";
                var subs = new List<KeyValuePair<string, string>>();
                foreach (Match m in PairRegex().Matches(html))
                {
                    var key = Clean(m.Groups[1].Value);
                    var value = Clean(m.Groups[2].Value);
                    if (key.Length == 0 || value.Length == 0) continue;
                    if (key.Contains("serial", StringComparison.OrdinalIgnoreCase)) serial = value;
                    else if (key.StartsWith("Main Firmware", StringComparison.OrdinalIgnoreCase)) main = value;
                    else if (key.StartsWith("Sub", StringComparison.OrdinalIgnoreCase) &&
                             key.Contains("firmware", StringComparison.OrdinalIgnoreCase))
                        subs.Add(new KeyValuePair<string, string>(key, value));
                }
                if (serial.Length > 0 || main.Length > 0 || subs.Count > 0)
                    return new BrotherInfo(serial, main, subs);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // not reachable on this scheme / not a Brother EWS – try the next, then give up
            }
        }
        return null;
    }

    /// <summary>Strips inner tags (e.g. the &lt;span class="unit"&gt;), decodes entities, trims.</summary>
    private static string Clean(string s) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " ")), @"\s+", " ").Trim();
}
