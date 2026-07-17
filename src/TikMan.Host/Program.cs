using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using TikMan.Core.Storage;
using TikMan.Web;

namespace TikMan.Host;

/// <summary>Headless TikMan entry point: starts the built-in web server (the same dashboard the Windows
/// GUI hosts) and keeps running until Ctrl+C. This is what a Linux/macOS user launches.
/// <para>Basic auth is mandatory – without a user and password the server refuses to start, exactly
/// like the GUI. Credentials come, in order, from CLI flags, environment variables, or the saved
/// settings; the password is only ever held to authenticate and is never logged.</para></summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var opts = Options.Parse(args);
        if (opts.ShowHelp) { PrintHelp(); return 0; }

        var app = DeviceStore.Load();
        var user = opts.User ?? Environment.GetEnvironmentVariable("TIKMAN_WEB_USER") ?? (app.WebServerUser ?? "");
        var pass = opts.Password ?? Environment.GetEnvironmentVariable("TIKMAN_WEB_PASS")
                   ?? CredentialProtector.Unprotect(app.WebServerEncryptedPassword);
        user = user.Trim();

        if (user.Length == 0 || pass.Length == 0)
        {
            Console.Error.WriteLine("TikMan headless needs a web login (HTTP Basic auth is mandatory).");
            Console.Error.WriteLine("Provide one via  --user <name> --pass <secret>,  the TIKMAN_WEB_USER /");
            Console.Error.WriteLine("TIKMAN_WEB_PASS environment variables, or the Windows GUI's Web settings.");
            return 2;
        }

        var port = opts.Port ?? (app.WebServerPort is > 0 and < 65536 ? app.WebServerPort : 9090);
        var useHttps = !opts.ForceHttp;

        X509Certificate2? cert = null;
        if (useHttps)
        {
            try
            {
                cert = WebCertificate.LoadOrCreate(app.WebServerCertPath?.Trim() ?? "",
                    CredentialProtector.Unprotect(app.WebServerCertPassword));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"HTTPS certificate could not be prepared ({ex.Message}); falling back to HTTP.");
                Console.Error.WriteLine("Pass --http to silence this, or set a certificate in the settings.");
                cert = null;
            }
        }

        var backend = new HostBackend();
        var server = new WebServer(backend, port, user, pass, cert);
        try { server.Start(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not start the web server on port {port}: {ex.Message}");
            return 3;
        }

        Console.WriteLine($"TikMan headless {backend.AppVersion} — {backend.GetDevices().Count} device(s) loaded.");
        Console.WriteLine($"Dashboard:  {server.BoundUrl}");
        if (cert is null && useHttps) Console.WriteLine("(running over plain HTTP — password-bearing actions are disabled)");
        if (!opts.NoBrowser) TryOpenBrowser(server.BoundUrl);
        Console.WriteLine("Press Ctrl+C to stop.");

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();
        stop.Wait();

        Console.WriteLine("Stopping…");
        server.Stop();
        return 0;
    }

    /// <summary>Opens the dashboard in the platform's default browser (best-effort – on a headless box
    /// there may be none, and the printed URL is the fallback).</summary>
    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch { /* no browser / headless – the URL is printed above */ }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            TikMan headless — network device dashboard (Linux/macOS/Windows)

            Usage: tikman-host [options]
              --port <n>        listen port (default: saved setting or 9090)
              --user <name>     web login user (or env TIKMAN_WEB_USER)
              --pass <secret>   web login password (or env TIKMAN_WEB_PASS)
              --http            serve plain HTTP instead of HTTPS (password actions then disabled)
              --no-browser      don't try to open a browser on start
              -h, --help        show this help

            The web login is mandatory (HTTP Basic auth). Open the printed URL in a browser.
            """);
    }

    private sealed class Options
    {
        public int? Port;
        public string? User;
        public string? Password;
        public bool ForceHttp;
        public bool NoBrowser;
        public bool ShowHelp;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var p): o.Port = p; break;
                    case "--user" when i + 1 < args.Length: o.User = args[++i]; break;
                    case "--pass" when i + 1 < args.Length: o.Password = args[++i]; break;
                    case "--http": o.ForceHttp = true; break;
                    case "--no-browser": o.NoBrowser = true; break;
                    case "-h" or "--help": o.ShowHelp = true; break;
                }
            }
            return o;
        }
    }
}
