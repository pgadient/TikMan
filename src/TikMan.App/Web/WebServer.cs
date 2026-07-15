using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TikMan.Core.Api;

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
                if (req is null) { }
                else if (IsWebSocketUpgrade(req)) await HandleWebSocketAsync(stream, req, ct).ConfigureAwait(false);
                else await RespondAsync(stream, req).ConfigureAwait(false);
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

    /// <summary>Parses an application/x-www-form-urlencoded body – credentials come in the POST body
    /// (not the query string, which tends to end up in logs). '+' means space, then percent-decode.</summary>
    private static Dictionary<string, string> ParseForm(byte[] body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = Encoding.UTF8.GetString(body);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[k] = v;
        }
        return result;
    }

    private static string Value(Dictionary<string, string> form, string key) =>
        form.TryGetValue(key, out var v) ? v : "";

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
            case "/api/topology":
                await WriteJsonAsync(stream, await _backend.GetTopologyAsync(QueryValue(req.Query, "view") == "physical"));
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
            case "/api/login":
                if (!IsPost(req.Method)) { await MethodNotAllowed(stream); break; }
                // A password must never travel over plain HTTP – this endpoint is HTTPS-only.
                if (!IsHttps)
                {
                    await WriteAsync(stream, 403, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes("{\"ok\":false,\"message\":\"HTTPS is required to set a login.\"}"));
                    break;
                }
                var form = ParseForm(req.Body);
                await WriteJsonAsync(stream, _backend.SetLogin(
                    Value(form, "id"), Value(form, "user"), Value(form, "password")));
                break;
            case "/api/backup":
                if (!IsPost(req.Method)) { await MethodNotAllowed(stream); break; }
                // A backup can contain device secrets – HTTPS only.
                if (!IsHttps)
                {
                    await WriteAsync(stream, 403, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes("{\"ok\":false,\"message\":\"HTTPS is required to download a backup.\"}"));
                    break;
                }
                var backup = await _backend.MakeBackupAsync(
                    QueryValue(req.Query, "id") ?? "", QueryValue(req.Query, "full") == "true");
                if (!backup.Ok)
                    await WriteAsync(stream, 502, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, message = backup.Message }, JsonOpts)));
                else
                    await WriteAsync(stream, 200, backup.ContentType, backup.Bytes,
                        $"Content-Disposition: attachment; filename=\"{backup.FileName}\"");
                break;
            case "/api/scan":
                if (!IsPost(req.Method)) { await MethodNotAllowed(stream); break; }
                _backend.StartScan();
                await WriteAsync(stream, 202, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"started\":true}"));
                break;
            case "/api/info":
                await WriteJsonAsync(stream, new { title = _backend.AppTitle, version = _backend.AppVersion });
                break;
            case "/xterm.js":
                await ServeAssetAsync(stream, "xterm.js", "application/javascript; charset=utf-8");
                break;
            case "/xterm-addon-fit.js":
                await ServeAssetAsync(stream, "xterm-addon-fit.js", "application/javascript; charset=utf-8");
                break;
            case "/xterm.css":
                await ServeAssetAsync(stream, "xterm.css", "text/css; charset=utf-8");
                break;
            case "/novnc.js":
                await ServeAssetAsync(stream, "novnc.js", "application/javascript; charset=utf-8");
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
        405 => "Method Not Allowed", 403 => "Forbidden", 502 => "Bad Gateway", _ => "OK",
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

    // ---- WebSocket: SSH terminal ----

    private static bool IsWebSocketUpgrade(Request req) =>
        req.Headers.TryGetValue("Upgrade", out var up) &&
        up.Contains("websocket", StringComparison.OrdinalIgnoreCase) &&
        req.Headers.ContainsKey("Sec-WebSocket-Key");

    /// <summary>Upgrades to a WebSocket and bridges it – to an SSH shell (/ws/ssh) or a raw VNC/RFB TCP
    /// connection (/ws/vnc). Auth + HTTPS are mandatory: both carry sensitive data (keystrokes, shell
    /// output, the screen and the VNC password), so neither ever runs over plain HTTP.</summary>
    private async Task HandleWebSocketAsync(Stream stream, Request req, CancellationToken ct)
    {
        if (!Authorised(req))
        {
            await WriteAsync(stream, 401, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("401 Unauthorized"),
                "WWW-Authenticate: Basic realm=\"TikMan\", charset=\"UTF-8\"");
            return;
        }
        if (!IsHttps)
        {
            await WriteAsync(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("HTTPS required"));
            return;
        }
        if ((req.Path != "/ws/ssh" && req.Path != "/ws/vnc") ||
            !req.Headers.TryGetValue("Sec-WebSocket-Key", out var wsKey))
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
            return;
        }

        // RFC 6455 handshake, then let the framework own the framing over our (TLS) stream.
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(wsKey.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var handshake = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                        "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        using var ws = WebSocket.CreateFromStream(stream,
            new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });

        var id = QueryValue(req.Query, "id") ?? "";
        if (req.Path == "/ws/vnc") { await ServeVncAsync(ws, id, ct); return; }

        var cols = ParseDim(QueryValue(req.Query, "cols"), 120);
        var rows = ParseDim(QueryValue(req.Query, "rows"), 32);

        ITerminalSession? session = null;
        try { session = await _backend.OpenSshShellAsync(id, cols, rows).ConfigureAwait(false); }
        catch { }
        if (session is null)
        {
            try
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes("\r\n[31mConnection failed.[0m\r\n"),
                    WebSocketMessageType.Binary, true, ct);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "no session", ct);
            }
            catch { }
            return;
        }
        using (session) await BridgeAsync(ws, session, ct);
    }

    private static uint ParseDim(string? s, uint fallback) =>
        uint.TryParse(s, out var v) && v is > 0 and <= 500 ? v : fallback;

    /// <summary>Transparently relays RFB bytes between the browser's noVNC (over the WebSocket) and the
    /// device's VNC TCP port – the server understands nothing of RFB, it only shuffles bytes. The target
    /// host+port is resolved from the device id server-side, so a client can't point the proxy elsewhere.</summary>
    private async Task ServeVncAsync(WebSocket ws, string id, CancellationToken ct)
    {
        NetEndpoint? target = null;
        try { target = _backend.GetVncTarget(id); } catch { }
        if (target is null || target.Port is <= 0 or > 65535)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "no vnc", ct); } catch { }
            return;
        }

        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(target.Host, target.Port, ct).ConfigureAwait(false); }
        catch
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "connect failed", ct); } catch { }
            return;
        }
        tcp.NoDelay = true;
        using var tcpStream = tcp.GetStream();
        await BridgeTcpAsync(ws, tcpStream, ct);
    }

    /// <summary>Pumps bytes both ways between a WebSocket and a raw TCP stream until either side closes.</summary>
    private static async Task BridgeTcpAsync(WebSocket ws, Stream tcp, CancellationToken ct)
    {
        var tcpToWs = Task.Run(async () =>
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = await tcp.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    await ws.SendAsync(new ArraySegment<byte>(buf, 0, n), WebSocketMessageType.Binary, true, ct)
                        .ConfigureAwait(false);
            }
            catch { }
        }, ct);

        var buf2 = new byte[16384];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                try { res = await ws.ReceiveAsync(new ArraySegment<byte>(buf2), ct).ConfigureAwait(false); }
                catch { break; }
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.Count > 0) await tcp.WriteAsync(buf2.AsMemory(0, res.Count), ct).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            try { tcp.Dispose(); } catch { } // unblocks the tcp→ws read
            try { await tcpToWs.ConfigureAwait(false); } catch { }
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>Pumps bytes both ways: shell output → WebSocket (through a queue, so SSH.NET's callback
    /// thread never touches the socket), WebSocket input → shell.</summary>
    private static async Task BridgeAsync(WebSocket ws, ITerminalSession session, CancellationToken ct)
    {
        var outbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        void OnData(byte[] d) => outbox.Writer.TryWrite(d);
        session.DataReceived += OnData;

        var sender = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await ws.SendAsync(chunk, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
            catch { }
        }, ct);

        var buf = new byte[16384];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                try { res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false); }
                catch { break; }
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.Count > 0) session.Write(buf, res.Count); // browser keystrokes → shell
            }
        }
        finally
        {
            session.DataReceived -= OnData;
            outbox.Writer.TryComplete();
            try { await sender.ConfigureAwait(false); } catch { }
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
    }

    // ---- embedded static assets (xterm.js …) ----

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> AssetCache = new();

    private static byte[]? EmbeddedAsset(string fileName) => AssetCache.GetOrAdd(fileName, fn =>
    {
        var asm = typeof(WebServer).Assembly;
        var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("." + fn, StringComparison.Ordinal));
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    private static async Task ServeAssetAsync(Stream stream, string file, string contentType)
    {
        var bytes = EmbeddedAsset(file);
        if (bytes is null)
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
        else
            await WriteAsync(stream, 200, contentType, bytes);
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
