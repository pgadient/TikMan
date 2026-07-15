using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TikMan.App.Web;

/// <summary>A tiny embedded HTTP(S) server built directly on <see cref="TcpListener"/> + optional
/// <see cref="SslStream"/> – deliberately NOT HttpListener/Kestrel:
/// <list type="bullet">
/// <item>No ASP.NET Core framework reference, so the framework-dependent builds stay slim and keep
/// starting on the plain .NET Desktop runtime.</item>
/// <item>TcpListener binds every interface WITHOUT an admin URL reservation (HttpListener needs a
/// one-time netsh urlacl for LAN, and http.sys sslcert + admin for HTTPS – both avoided here).</item>
/// <item>TLS is bound straight to the socket via SslStream with any X509 certificate (a generated
/// self-signed one, or the user's own), so HTTPS needs no certificate store or netsh at all.</item>
/// </list>
/// Every request must pass HTTP Basic auth against the configured user/password. When TLS is on, the
/// credential-carrying endpoints (added later) are refused over anything but HTTPS.</summary>
public sealed class WebServer : IDisposable
{
    private readonly IWebBackend _backend;
    private readonly string _user;
    private readonly string _password;
    private readonly int _port;
    private readonly X509Certificate2? _cert; // null → HTTP, otherwise HTTPS
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    // camelCase so the JSON keys match the dashboard's JavaScript (d.name, s.scanning, …).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The URL the server is listening on once started (e.g. https://192.168.0.5:9090/), else "".</summary>
    public string BoundUrl { get; private set; } = "";

    /// <summary>True when the server is serving over TLS.</summary>
    public bool IsHttps => _cert != null;

    public bool IsRunning => _running;

    public WebServer(IWebBackend backend, int port, string user, string password, X509Certificate2? certificate = null)
    {
        _backend = backend;
        _port = port;
        _user = user;
        _password = password;
        _cert = certificate;
    }

    /// <summary>Starts listening on every interface. Throws (SocketException) only if the port is taken –
    /// no admin, no URL reservation, no certificate store needed.</summary>
    public void Start()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _running = true;
        BoundUrl = $"{(IsHttps ? "https" : "http")}://{PreferredLocalAddress()}:{_port}/";
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (_running && listener is not null && !ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) { break; } // listener stopped/disposed
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            Stream stream = client.GetStream();
            SslStream? ssl = null;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(30)); // don't let a half-open client hang a task
            try
            {
                if (_cert is not null)
                {
                    ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(_cert, clientCertificateRequired: false,
                        checkCertificateRevocation: false).ConfigureAwait(false);
                    stream = ssl;
                }
                var req = await ReadRequestAsync(stream, readCts.Token).ConfigureAwait(false);
                if (req is not null) await RespondAsync(stream, req).ConfigureAwait(false);
            }
            catch (Exception) { /* client hiccup / TLS failure / timeout – just drop it */ }
            finally { ssl?.Dispose(); }
        }
    }

    // ---- request parsing ----

    private sealed record Request(string Method, string Path, string Query,
        IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var acc = new List<byte>(8192);
        int headerEnd;
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return null; // closed before a full request
            acc.AddRange(buffer.AsSpan(0, n).ToArray());
            headerEnd = IndexOfHeaderEnd(acc);
            if (headerEnd >= 0) break;
            if (acc.Count > 64 * 1024) return null; // absurd header – give up
        }

        var headerText = Encoding.ASCII.GetString(acc.GetRange(0, headerEnd).ToArray());
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        var method = requestLine[0];
        var target = requestLine[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? "" : target[(q + 1)..];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int contentLength = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
        contentLength = Math.Clamp(contentLength, 0, 1 << 20); // never buffer more than 1 MB of body

        var body = Array.Empty<byte>();
        if (contentLength > 0)
        {
            body = new byte[contentLength];
            int bodyStart = headerEnd + 4;
            int have = Math.Min(acc.Count - bodyStart, contentLength);
            if (have > 0) acc.CopyTo(bodyStart, body, 0, have);
            int read = Math.Max(have, 0);
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
        }
        return new Request(method, path, query, headers, body);
    }

    private static int IndexOfHeaderEnd(List<byte> b)
    {
        for (int i = 0; i + 3 < b.Count; i++)
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = eq < 0 ? pair : pair[..eq];
            if (Uri.UnescapeDataString(k) == key)
                return Uri.UnescapeDataString(eq < 0 ? "" : pair[(eq + 1)..]);
        }
        return null;
    }

    // ---- routing ----

    private async Task RespondAsync(Stream stream, Request req)
    {
        if (!Authorised(req))
        {
            await WriteAsync(stream, 401, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("401 Unauthorized"),
                "WWW-Authenticate: Basic realm=\"TikMan\", charset=\"UTF-8\"");
            return;
        }

        switch (req.Path)
        {
            case "/":
            case "/index.html":
                await WriteAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebAssets.IndexHtml));
                break;
            case "/api/devices":
                await WriteJsonAsync(stream, _backend.GetDevices());
                break;
            case "/api/status":
                await WriteJsonAsync(stream, _backend.GetStatus());
                break;
            case "/api/device":
                var detail = _backend.GetDevice(QueryValue(req.Query, "id") ?? "");
                if (detail is null)
                    await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
                else await WriteJsonAsync(stream, detail);
                break;
            case "/api/wake":
                if (!IsPost(req.Method)) { await MethodNotAllowed(stream); break; }
                await WriteJsonAsync(stream, _backend.Wake(QueryValue(req.Query, "id") ?? ""));
                break;
            case "/api/scan":
                if (!IsPost(req.Method)) { await MethodNotAllowed(stream); break; }
                _backend.StartScan();
                await WriteAsync(stream, 202, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"started\":true}"));
                break;
            case "/api/info":
                await WriteJsonAsync(stream, new { title = _backend.AppTitle, version = _backend.AppVersion });
                break;
            default:
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
                break;
        }
    }

    private static bool IsPost(string method) => method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    private static Task MethodNotAllowed(Stream s) =>
        WriteAsync(s, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("405 Method Not Allowed"));

    // ---- responses ----

    private static Task WriteJsonAsync(Stream s, object value) =>
        WriteAsync(s, 200, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOpts)));

    private static async Task WriteAsync(Stream s, int status, string contentType, byte[] body, params string[] extraHeaders)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        foreach (var h in extraHeaders) head.Append(h).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        if (body.Length > 0) await s.WriteAsync(body).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 401 => "Unauthorized", 404 => "Not Found",
        405 => "Method Not Allowed", 403 => "Forbidden", _ => "OK",
    };

    // ---- auth ----

    /// <summary>Constant-time check of the request's "Basic base64(user:pass)" header against the
    /// configured credentials. Empty configured user/password means deny-all (the app refuses to start
    /// the server without credentials, but this guards it anyway).</summary>
    private bool Authorised(Request req)
    {
        if (_user.Length == 0 || _password.Length == 0) return false;
        if (!req.Headers.TryGetValue("Authorization", out var header) ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())); }
        catch (FormatException) { return false; }
        var sep = decoded.IndexOf(':');
        if (sep < 0) return false;
        return FixedEquals(decoded[..sep], _user) & FixedEquals(decoded[(sep + 1)..], _password);
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string PreferredLocalAddress()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch { }
        return Dns.GetHostName();
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        BoundUrl = "";
    }

    public void Dispose() => Stop();
}
