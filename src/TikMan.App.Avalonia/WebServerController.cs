using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using TikMan.Core.Api;
using TikMan.Core.Fleet;
using TikMan.Core.Storage;
using TikMan.Web;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

/// <summary>Starts/stops the built-in web server for this GUI. The server gets a <see cref="HostBackend"/>
/// wrapped around the GUI's <b>live</b> fleet, so the browser view mirrors what is on screen instead of
/// scanning a second time.
/// <para>Credentials are mandatory (the server refuses to start without them) and the web password is only
/// ever read back DPAPI/AES-decrypted at start time – never logged, never rendered.</para></summary>
public sealed class WebServerController
{
    private readonly FleetService _fleet;
    private readonly AppData _appData;
    private WebServer? _server;

    public WebServerController(FleetService fleet, AppData appData)
    {
        _fleet = fleet;
        _appData = appData;
    }

    public bool IsRunning => _server?.IsRunning == true;
    public string Url => _server?.BoundUrl ?? "";

    /// <summary>Starts the server; returns a status message for the UI. Fails cleanly (never throws) so a
    /// busy port or a bad certificate can't take the app down.</summary>
    public string Start()
    {
        if (IsRunning) return T("Av_WebRunning", Url);

        var user = _appData.WebServerUser?.Trim() ?? "";
        var pass = CredentialProtector.Unprotect(_appData.WebServerEncryptedPassword);
        if (user.Length == 0 || pass.Length == 0) return T("Av_WebNeedCreds");

        X509Certificate2? cert = null;
        if (_appData.WebServerUseHttps)
        {
            try
            {
                cert = WebCertificate.LoadOrCreate(_appData.WebServerCertPath?.Trim() ?? "",
                    CredentialProtector.Unprotect(_appData.WebServerCertPassword));
            }
            catch { cert = null; } // fall back to plain HTTP; the server then disables password actions
        }

        try
        {
            var port = _appData.WebServerPort is > 0 and <= 65535 ? _appData.WebServerPort : 9090;
            var server = new WebServer(new HostBackend(_fleet, _appData), port, user, pass, cert);
            server.Start();
            _server = server;
            return T("Av_WebRunning", server.BoundUrl);
        }
        catch (Exception ex) { return T("Av_WebFailed", ex.Message); }
    }

    public string Stop()
    {
        // Dispose typically calls Stop again internally, so it needs the same guard – a throwing double-stop
        // otherwise escaped into a plain (non-async) click handler and reached the dispatcher unhandled.
        var server = _server;
        _server = null;
        try { server?.Stop(); } catch { /* already down */ }
        try { server?.Dispose(); } catch { /* ditto */ }
        return T("Av_WebStopped");
    }

    /// <summary>Opens the running dashboard in the default browser (cross-platform).</summary>
    public void OpenInBrowser()
    {
        var url = Url;
        if (url.Length == 0) return;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch { /* no browser handler – the URL is shown in the UI anyway */ }
    }
}
