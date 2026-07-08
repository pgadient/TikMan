namespace TikMan.Core.Api;

public readonly record struct ReleaseInfo(string Version, DateTime ReleaseDate);

/// <summary>Queries the public MikroTik upgrade server for the release date of the
/// newest version per channel. The files under
/// https://upgrade.mikrotik.com/routeros/NEWESTa7.&lt;channel&gt; contain
/// "&lt;version&gt; &lt;unix-timestamp&gt;", e.g. "7.23.1 1780392316".</summary>
public static class ReleaseInfoClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly HashSet<string> KnownChannels =
        new(StringComparer.OrdinalIgnoreCase) { "stable", "long-term", "testing", "development" };

    /// <summary>Returns the version and release date for the channel, or null for an
    /// unknown channel / no internet connection.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(string channel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channel) || !KnownChannels.Contains(channel))
            return null;

        try
        {
            var text = (await Http.GetStringAsync(
                $"https://upgrade.mikrotik.com/routeros/NEWESTa7.{channel.ToLowerInvariant()}", ct)
                .ConfigureAwait(false)).Trim();

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var unix))
                return new ReleaseInfo(parts[0], DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // offline or timeout – the release date is optional
        }
        return null;
    }
}
