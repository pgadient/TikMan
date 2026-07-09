using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>Fingerprints a host's web interface: the HTTP <c>Server</c> header (nginx, IIS, …)
/// and, for consumer gateways, a product name pulled from the page &lt;title&gt;. Best-effort and
/// read-only — one short GET, self-signed certificates accepted, empty strings when nothing shows.</summary>
public static partial class HttpFingerprint
{
    public readonly record struct HttpInfo(string WebServer, string Model);

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    // Title fragments (lower-case) that mark the page as a router/gateway login → use it as the model.
    private static readonly string[] GatewayHints =
    {
        "internet-box", "internet box", "fritz!box", "fritzbox", "livebox", "speedport",
        "connect box", "salt", "sunrise", "gateway", "router", "modem", "-box",
    };

    public static async Task<HttpInfo> ProbeAsync(string host, CancellationToken ct = default)
    {
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host; // bracket IPv6
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // gateways use self-signed certs
                    AllowAutoRedirect = true,
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TikMan/1.0");

                using var resp = await http.GetAsync($"{scheme}://{hostPart}/",
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                var server = (resp.Headers.Server?.ToString() ?? "").Trim();
                var model = DetectModel(await ReadTitleAsync(resp, ct).ConfigureAwait(false));
                if (server.Length > 0 || model.Length > 0) return new HttpInfo(server, model);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // unreachable on this scheme – try the next, then give up
            }
        }
        return new HttpInfo("", "");
    }

    private static async Task<string> ReadTitleAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[16384];
            int read = 0, n;
            while (read < buffer.Length &&
                   (n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false)) > 0)
                read += n;
            var html = Encoding.UTF8.GetString(buffer, 0, read);
            var m = TitleRegex().Match(html);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return ""; }
    }

    private static string DetectModel(string title)
    {
        if (title.Length is 0 or > 60) return ""; // empty or clearly not a model string
        var lower = title.ToLowerInvariant();
        foreach (var hint in GatewayHints)
            if (lower.Contains(hint)) return title;
        return "";
    }
}
