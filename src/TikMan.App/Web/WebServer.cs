using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TikMan.App.Web;

/// <summary>A tiny embedded HTTP server (built on <see cref="HttpListener"/>, so no ASP.NET Core
/// framework dependency – the framework-dependent builds stay slim and keep starting on the plain
/// .NET Desktop runtime). It serves a small dashboard plus a JSON API over the running app's live
/// state. Every request must pass HTTP Basic auth against the configured user/password.</summary>
public sealed class WebServer : IDisposable
{
    private readonly IWebBackend _backend;
    private readonly string _user;
    private readonly string _password;
    private readonly int _port;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    // camelCase so the JSON keys match the dashboard's JavaScript (d.name, s.scanning, …).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The URL the server is actually listening on once started (LAN or localhost), else "".</summary>
    public string BoundUrl { get; private set; } = "";

    /// <summary>True when it could only bind to localhost (no LAN URL reservation) – the UI then tells
    /// the user their phone can't reach it until they grant the one-time reservation.</summary>
    public bool LocalOnly { get; private set; }

    public bool IsRunning => _listener?.IsListening == true;

    public WebServer(IWebBackend backend, int port, string user, string password)
    {
        _backend = backend;
        _port = port;
        _user = user;
        _password = password;
    }

    /// <summary>Starts listening. Tries all interfaces first (reachable from other devices); if the OS
    /// refuses that without a URL reservation, falls back to localhost so the server still comes up.
    /// Throws only if even localhost fails (e.g. the port is already taken).</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();

        // Prefer "http://+:{port}/" – reachable across the LAN. That prefix needs a one-time netsh
        // urlacl on Windows; without it HttpListener.Start throws access-denied, so we drop to loopback.
        if (TryListen($"http://+:{_port}/"))
        {
            BoundUrl = $"http://{PreferredLocalAddress()}:{_port}/";
            LocalOnly = false;
        }
        // Loopback fallback: register BOTH the "localhost" host token and the 127.0.0.1 literal, else
        // http.sys answers requests to the other form with "400 Invalid Hostname". Both bind without admin.
        else if (TryListen($"http://localhost:{_port}/", $"http://127.0.0.1:{_port}/"))
        {
            BoundUrl = $"http://localhost:{_port}/";
            LocalOnly = true;
        }
        else
        {
            throw new InvalidOperationException($"Port {_port} could not be opened.");
        }

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private bool TryListen(params string[] prefixes)
    {
        try
        {
            var listener = new HttpListener();
            foreach (var prefix in prefixes) listener.Prefixes.Add(prefix);
            listener.Start();
            _listener = listener;
            return true;
        }
        catch (HttpListenerException) { return false; } // access denied (no urlacl) or port in use
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>The command a user runs once (elevated) to let the LAN reach the server. Shown in the UI
    /// when we could only bind to localhost.</summary>
    public static string ReservationCommand(int port) =>
        $"netsh http add urlacl url=http://+:{port}/ user=\"{Environment.UserDomainName}\\{Environment.UserName}\"";

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { break; } // listener stopped/disposed
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!IsAuthorised(ctx.Request))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.AddHeader("WWW-Authenticate", "Basic realm=\"TikMan\", charset=\"UTF-8\"");
                await WriteAsync(ctx, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("401 Unauthorized"));
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/":
                case "/index.html":
                    await WriteAsync(ctx, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebAssets.IndexHtml));
                    break;
                case "/api/devices":
                    await WriteJsonAsync(ctx, _backend.GetDevices());
                    break;
                case "/api/status":
                    await WriteJsonAsync(ctx, _backend.GetStatus());
                    break;
                case "/api/device":
                    var detail = _backend.GetDevice(ctx.Request.QueryString["id"] ?? "");
                    if (detail is null)
                    {
                        ctx.Response.StatusCode = 404;
                        await WriteAsync(ctx, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
                    }
                    else await WriteJsonAsync(ctx, detail);
                    break;
                case "/api/wake":
                    if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Response.StatusCode = 405;
                        await WriteAsync(ctx, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("405 Method Not Allowed"));
                        break;
                    }
                    await WriteJsonAsync(ctx, _backend.Wake(ctx.Request.QueryString["id"] ?? ""));
                    break;
                case "/api/scan":
                    if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Response.StatusCode = 405;
                        await WriteAsync(ctx, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("405 Method Not Allowed"));
                        break;
                    }
                    _backend.StartScan();
                    ctx.Response.StatusCode = 202;
                    await WriteAsync(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"started\":true}"));
                    break;
                case "/api/info":
                    await WriteJsonAsync(ctx, new { title = _backend.AppTitle, version = _backend.AppVersion });
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    await WriteAsync(ctx, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
                    break;
            }
        }
        catch (Exception) { try { ctx.Response.Abort(); } catch { } }
    }

    private static Task WriteJsonAsync(HttpListenerContext ctx, object value) =>
        WriteAsync(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOpts)));

    private static async Task WriteAsync(HttpListenerContext ctx, string contentType, byte[] body)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        ctx.Response.Close();
    }

    /// <summary>Constant-time check of the "Basic base64(user:pass)" header against the configured
    /// credentials. An empty configured user/password is treated as "deny all" – the app refuses to
    /// start the server without credentials, but guard here too.</summary>
    private bool IsAuthorised(HttpListenerRequest req)
    {
        if (_user.Length == 0 || _password.Length == 0) return false;
        var header = req.Headers["Authorization"];
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())); }
        catch (FormatException) { return false; }
        var sep = decoded.IndexOf(':');
        if (sep < 0) return false;
        var user = decoded[..sep];
        var pass = decoded[(sep + 1)..];
        return FixedEquals(user, _user) & FixedEquals(pass, _password); // non-short-circuit: same work either way
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Best-effort primary IPv4 of this machine, for a copy-pasteable LAN URL.</summary>
    private static string PreferredLocalAddress()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch { }
        return Dns.GetHostName();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        BoundUrl = "";
    }

    public void Dispose() => Stop();
}
