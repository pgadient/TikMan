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

        // First-run bootstrap: with nothing configured (the double-click case – no CLI, no env, no
        // saved login), generate a strong password once, persist it, and write it to a plain-text file
        // beside the settings so the user can read it. Without this a double-clicked app would just
        // exit "needs a login". --no-autologin opts out (back to the strict error) for a server setup.
        string? loginFile = null;
        if ((user.Length == 0 || pass.Length == 0) && !opts.NoAutoLogin)
        {
            user = user.Length > 0 ? user : "tikman";
            pass = GeneratePassword();
            app.WebServerUser = user;
            app.WebServerEncryptedPassword = CredentialProtector.Protect(pass);
            try { DeviceStore.Save(app); } catch { /* not persisting = a new password next start */ }
            loginFile = WriteLoginFile(user, pass);
        }

        if (user.Length == 0 || pass.Length == 0)
        {
            Console.Error.WriteLine("TikMan headless needs a web login (HTTP Basic auth is mandatory).");
            Console.Error.WriteLine("Provide one via  --user <name> --pass <secret>,  the TIKMAN_WEB_USER /");
            Console.Error.WriteLine("TIKMAN_WEB_PASS environment variables, or drop --no-autologin to have");
            Console.Error.WriteLine("one generated for you on first run.");
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
        if (loginFile is not null)
        {
            Console.WriteLine();
            Console.WriteLine("  A web login was generated for this first run:");
            Console.WriteLine($"    user:     {user}");
            Console.WriteLine($"    password: {pass}");
            Console.WriteLine($"  (also saved to {loginFile})");
            Console.WriteLine();
            if (!opts.NoBrowser) TryOpen(loginFile); // pop it open (TextEdit/Notepad) so it's not missed
        }
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

    /// <summary>A readable random password: 20 chars from an unambiguous alphabet (no 0/O/1/l/I), so it
    /// can be retyped from the login file without confusion. ~103 bits of entropy – plenty for a LAN.</summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[20];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Writes the generated login to a plain file next to the settings (owner-only on Unix), so
    /// a user who double-clicked the app – and never saw the console – can still find the password.</summary>
    private static string? WriteLoginFile(string user, string pass)
    {
        try
        {
            var path = Path.Combine(DeviceStore.StorageDirectory, "tikman-login.txt");
            Directory.CreateDirectory(DeviceStore.StorageDirectory);
            // LF line endings – the primary readers are Unix editors (this is the double-click case on
            // Linux/macOS); modern Windows Notepad reads LF fine too.
            File.WriteAllText(path,
                $"TikMan web login\n----------------\nuser:     {user}\npassword: {pass}\n\n" +
                "Delete this file once you have noted the password. To change the login, set it in the\n" +
                "Windows GUI's Web settings, or run tikman-host with --user and --pass.\n");
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }
        catch { return null; } // couldn't write – the console print is still there
    }

    /// <summary>Opens a file with the OS default handler (TextEdit/Notepad) – best-effort.</summary>
    private static void TryOpen(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", path);
            else
                Process.Start("xdg-open", path);
        }
        catch { /* headless / no handler – the console and the file itself remain */ }
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
              --no-autologin    don't generate a login on first run (require --user/--pass instead)
              -h, --help        show this help

            The web login is mandatory (HTTP Basic auth). On first run without one, a password is
            generated, saved, and written to tikman-login.txt next to the settings.
            """);
    }

    private sealed class Options
    {
        public int? Port;
        public string? User;
        public string? Password;
        public bool ForceHttp;
        public bool NoBrowser;
        public bool NoAutoLogin;
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
                    case "--no-autologin": o.NoAutoLogin = true; break;
                    case "-h" or "--help": o.ShowHelp = true; break;
                }
            }
            return o;
        }
    }
}
