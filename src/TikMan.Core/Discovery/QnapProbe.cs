using System.Text.RegularExpressions;

namespace TikMan.Core.Discovery;

/// <summary>Detects a QNAP NAS via its QTS/QuTS login endpoint (<c>/cgi-bin/authLogin.cgi</c>,
/// usually on port 8080), which answers without a login with an XML document. The exact model is
/// only exposed after login, but the vendor, OS and hostname are available. Read-only, best-effort.</summary>
public static partial class QnapProbe
{
    public readonly record struct QnapInfo(string Hostname, string Os, string Model);

    [GeneratedRegex(@"<hostname>\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</hostname>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HostnameRegex();

    [GeneratedRegex(@"<(?:modelName|internalModelName|displayModelName)>\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ModelRegex();

    public static async Task<QnapInfo?> QueryAsync(string host, IReadOnlyCollection<int> openPorts, CancellationToken ct = default)
    {
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TikMan/1.0");

        // QTS commonly lives on 8080 (http) / 443 (https); try the open ones.
        var urls = new List<string>();
        if (openPorts.Contains(8080)) urls.Add($"http://{hostPart}:8080/cgi-bin/authLogin.cgi");
        if (openPorts.Contains(443)) urls.Add($"https://{hostPart}/cgi-bin/authLogin.cgi");
        if (openPorts.Contains(80)) urls.Add($"http://{hostPart}/cgi-bin/authLogin.cgi");

        foreach (var url in urls)
        {
            try
            {
                var xml = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                if (!xml.Contains("QDocRoot", StringComparison.OrdinalIgnoreCase)) continue; // not QNAP
                var hostname = HostnameRegex().Match(xml).Groups[1].Value.Trim();
                var model = ModelRegex().Match(xml).Groups[1].Value.Trim();
                return new QnapInfo(hostname, "QTS", model);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // try the next url
            }
        }
        return null;
    }
}
