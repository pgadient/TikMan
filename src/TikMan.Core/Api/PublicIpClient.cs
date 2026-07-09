using System.Net;

namespace TikMan.Core.Api;

/// <summary>Looks up the host's public IPv4 and IPv6 address via ipify (best-effort; empty on
/// failure, e.g. no IPv6 connectivity). This sends one request to an external service, whose only
/// content is the request itself — that's how the public address is observed.</summary>
public static class PublicIpClient
{
    public readonly record struct PublicIp(string V4, string V6);

    public static async Task<PublicIp> GetAsync(CancellationToken ct = default)
    {
        var v4 = await FetchAsync("https://api.ipify.org", ct).ConfigureAwait(false);
        var v6 = await FetchAsync("https://api6.ipify.org", ct).ConfigureAwait(false);
        return new PublicIp(v4, v6);
    }

    private static async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var text = (await http.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out _) ? text : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ""; // offline / no such address family
        }
    }
}
