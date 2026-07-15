using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TikMan.Core.Models;

namespace TikMan.Core.Api;

/// <summary>Client for the RouterOS v7 REST API (https://host/rest/...).</summary>
public sealed class RouterOsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <summary>Global switch: accept self-signed / invalid HTTPS certificates for all connections
    /// (default on – MikroTik ships a self-signed cert on a LAN). Set from the app's settings.</summary>
    public static bool AllowInsecureCertificates { get; set; } = true;

    /// <summary>Global switch: may credentials/data travel over plain HTTP when no secure transport
    /// (HTTPS/SSH) works? Off by default – TikMan never sends anything sensitive over HTTP unless the
    /// user turns this on in the settings. Set from the app's settings.</summary>
    public static bool AllowHttpFallback { get; set; }

    public RouterOsClient(string host, int port, bool useHttps, string username, string password, bool ignoreCertErrors)
    {
        var handler = new HttpClientHandler();
        if (useHttps && (ignoreCertErrors || AllowInsecureCertificates))
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        // Timeouts are controlled per request via CancellationToken.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        // IPv6 literals must be bracketed in a URL (e.g. https://[fe80::1]:443/rest/).
        var hostPart = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        _baseUrl = $"{(useHttps ? "https" : "http")}://{hostPart}:{port}/rest/";
    }

    public static RouterOsClient For(Device device, string password) =>
        new(device.Host, device.Port, device.UseHttps, device.Username, password, device.IgnoreCertErrors);

    public async Task<ResourceInfo> GetSystemResourceAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("system/resource", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var e = doc.RootElement;
        return new ResourceInfo
        {
            Version = S(e, "version"),
            BoardName = S(e, "board-name"),
            Platform = S(e, "platform"),
            ArchitectureName = S(e, "architecture-name"),
            Uptime = S(e, "uptime"),
            CpuLoad = ParseInt(S(e, "cpu-load")),
            FreeMemory = ParseLong(S(e, "free-memory")),
            TotalMemory = ParseLong(S(e, "total-memory")),
            FreeHddSpace = ParseLong(S(e, "free-hdd-space")),
            TotalHddSpace = ParseLong(S(e, "total-hdd-space")),
        };
    }

    /// <summary>Reads the RouterBOARD serial number ("" on CHR/x86 installs without a board).</summary>
    public async Task<string> GetSerialNumberAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("system/routerboard", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return S(doc.RootElement, "serial-number");
    }

    public async Task<string> GetIdentityAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("system/identity", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return S(doc.RootElement, "name");
    }

    /// <summary>The neighbour table (MNDP/LLDP/CDP): which MAC was heard on which interface. The
    /// interface field can be a comma list ("bridge,ether5"); the physical member is what places the
    /// neighbour, so bridge/vlan entries are skipped in favour of the port.</summary>
    public async Task<List<(string Mac, string Interface)>> GetNeighborsAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("ip/neighbor", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var list = new List<(string, string)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var mac = S(e, "mac-address");
            var ifaceList = S(e, "interface");
            if (mac.Length == 0 || ifaceList.Length == 0) continue;
            var parts = ifaceList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var port = parts.FirstOrDefault(p =>
                           !p.StartsWith("bridge", StringComparison.OrdinalIgnoreCase) &&
                           !p.StartsWith("vlan", StringComparison.OrdinalIgnoreCase))
                       ?? parts.LastOrDefault() ?? "";
            if (port.Length > 0) list.Add((mac, port));
        }
        return list;
    }

    /// <summary>Wireless interface name → SSID, for labelling the topology's wlan ports. Tries the v7
    /// wifi package first (hAP ax & co.), then the legacy wireless one; an empty map when the device
    /// has no radios.</summary>
    public async Task<Dictionary<string, string>> GetWifiSsidsAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { "interface/wifi", "interface/wireless" })
        {
            try
            {
                using var doc = await GetAsync(path, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var name = S(e, "name");
                    if (name.Length == 0) continue;
                    // The SSID sits flat on legacy wireless and on a non-CAPsMAN v7 wifi (either as
                    // "ssid" or the dotted "configuration.ssid"). A CAPsMAN-managed interface carries it
                    // only in the ".about" summary ("mode: AP, SSID: icn [S], channel: …"). Take the SSID
                    // verbatim – a trailing "[S]"/"[G]" is part of the network name the user chose, not
                    // an annotation to strip (it's what shows on a client when connecting).
                    var ssid = S(e, "ssid");
                    if (ssid.Length == 0) ssid = S(e, "configuration.ssid");
                    if (ssid.Length == 0)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(S(e, ".about"), @"SSID:\s*(.+?)(?:,\s*\w+:|$)");
                        if (m.Success) ssid = m.Groups[1].Value.Trim();
                    }
                    if (ssid.Length > 0) map[name] = ssid;
                }
                if (map.Count > 0) return map;
            }
            catch (Exception) { /* this package isn't installed on the device – try the other */ }
        }
        return map;
    }

    /// <summary>The bridge host (forwarding) table: which MAC is reachable behind which bridge port.
    /// This is the one thing that can *prove* which switch port a device hangs off – switching is
    /// layer 2 and therefore invisible to traceroute. Local entries (the bridge's own MACs) are
    /// skipped.</summary>
    public async Task<List<(string Mac, string Port)>> GetBridgeHostsAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("interface/bridge/host", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var list = new List<(string, string)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (S(e, "local") == "true") continue;                      // the bridge itself
            var mac = S(e, "mac-address");
            var port = S(e, "on-interface") is { Length: > 0 } p ? p : S(e, "interface");
            if (mac.Length > 0 && port.Length > 0) list.Add((mac, port));
        }
        return list;
    }

    /// <summary>Reads the device log; when maxEntries &gt; 0 only the last N entries.</summary>
    public async Task<List<LogEntry>> GetLogAsync(int maxEntries = 0, CancellationToken ct = default)
    {
        using var doc = await GetAsync("log", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var list = new List<LogEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            list.Add(new LogEntry
            {
                Time = S(item, "time"),
                Topics = S(item, "topics"),
                Message = S(item, "message"),
            });
        }
        if (maxEntries > 0 && list.Count > maxEntries)
            list.RemoveRange(0, list.Count - maxEntries);
        return list;
    }

    public async Task<UpdateInfo> GetUpdateStatusAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("system/package/update", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var e = doc.RootElement;
        return new UpdateInfo
        {
            Channel = S(e, "channel"),
            InstalledVersion = S(e, "installed-version"),
            LatestVersion = S(e, "latest-version"),
            Status = S(e, "status"),
        };
    }

    /// <summary>Queries the MikroTik update server for new versions and returns the status afterwards.</summary>
    public async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        (await PostAsync("system/package/update/check-for-updates", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false))?.Dispose();
        return await GetUpdateStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Switches the update channel (stable / testing / long-term / development)
    /// and then checks for updates immediately afterwards.</summary>
    public async Task<UpdateInfo> SetChannelAndCheckAsync(string channel, CancellationToken ct = default)
    {
        var body = $"{{\"channel\":\"{channel}\"}}";
        try
        {
            // Settable menu in RouterOS REST: POST to .../set
            await PostRawAsync("system/package/update/set", body, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (RouterOsApiException ex) when (ex.StatusCode is 404 or 400 or 405)
        {
            // Fallback for differing REST variants: PATCH the menu itself
            await SendAsync(new HttpMethod("PATCH"), "system/package/update", body, TimeSpan.FromSeconds(15), ct)
                .ConfigureAwait(false);
        }
        return await CheckForUpdatesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Loads the current configuration as a text export (.rsc). Sensitive values
    /// (passwords/keys) are hidden in it, just as in the standard RouterOS export.
    ///
    /// RouterOS does NOT return the export in the response body over REST – the command
    /// only writes it to a file. Therefore: export to a temporary file,
    /// read out its "contents" field and delete the file again.</summary>
    public async Task<string> GetConfigExportAsync(CancellationToken ct = default)
    {
        const string baseName = "mtmonitor-export";
        const string fileName = baseName + ".rsc";

        // 1) Write the export to a file (the response is expected to be empty)
        await PostRawAsync("export", $"{{\"file\":\"{baseName}\"}}", TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);

        // 2) Find the file and read out "contents" (may lag briefly)
        string id = "", contents = "";
        for (int attempt = 0; attempt < 8 && contents.Length == 0; attempt++)
        {
            if (attempt > 0) await Task.Delay(500, ct).ConfigureAwait(false);
            using var doc = await GetAsync("file", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (S(el, "name") != fileName) continue;
                id = S(el, ".id");
                contents = S(el, "contents");
                break;
            }
        }

        // 3) Remove the temp file (best effort)
        if (id.Length > 0)
        {
            try { await SendAsync(HttpMethod.Delete, "file/" + Uri.EscapeDataString(id), "", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch { /* Cleanup must not affect the result */ }
        }

        return contents;
    }

    /// <summary>Creates a binary full backup (.backup) on the device.</summary>
    public Task CreateBinaryBackupAsync(string name, CancellationToken ct = default) =>
        PostRawAsync("system/backup/save", $"{{\"name\":\"{name}\"}}", TimeSpan.FromSeconds(90), ct);

    /// <summary>Finds a file by name and returns its ".id" (empty if not present).</summary>
    public async Task<string> FindFileIdAsync(string fileName, CancellationToken ct = default)
    {
        using var doc = await GetAsync("file", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var el in doc.RootElement.EnumerateArray())
                if (S(el, "name") == fileName) return S(el, ".id");
        return "";
    }

    /// <summary>Waits until the file appears on the device; returns its ".id" or empty.</summary>
    public async Task<string> WaitForFileIdAsync(string fileName, int attempts, TimeSpan delay, CancellationToken ct = default)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
            var id = await FindFileIdAsync(fileName, ct).ConfigureAwait(false);
            if (id.Length > 0) return id;
        }
        return "";
    }

    public async Task DeleteFileAsync(string id, CancellationToken ct = default)
    {
        if (id.Length == 0) return;
        await SendAsync(HttpMethod.Delete, "file/" + Uri.EscapeDataString(id), "", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>Starts the update installation. The device downloads the packages and reboots –
    /// a connection drop during the request is the normal case and is swallowed.</summary>
    public async Task InstallUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            (await PostAsync("system/package/update/install", TimeSpan.FromMinutes(5), ct).ConfigureAwait(false))?.Dispose();
        }
        catch (RouterOsApiException)
        {
            throw; // do not hide genuine API errors (e.g. missing permissions)
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            // expected: the reboot cuts the connection
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // request timeout during download/reboot – likewise expected
        }
    }

    private Task<string> PostRawAsync(string path, string json, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, json, timeout, ct);

    private async Task<string> SendAsync(HttpMethod method, string path, string json, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        if (json.Length > 0)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, cts.Token).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await _http.GetAsync(_baseUrl + path, cts.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, cts.Token).ConfigureAwait(false);
        var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> PostAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_baseUrl + path, content, cts.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, cts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        string message = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var detail = S(doc.RootElement, "detail");
            if (detail == "") detail = S(doc.RootElement, "message");
            if (detail != "") message += $" – {detail}";
        }
        catch { /* the error body is optional */ }

        throw new RouterOsApiException((int)resp.StatusCode, message);
    }

    // RouterOS REST returns all values as strings.
    private static string S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;
    private static long ParseLong(string s) => long.TryParse(s, out var v) ? v : 0;

    public void Dispose() => _http.Dispose();
}
