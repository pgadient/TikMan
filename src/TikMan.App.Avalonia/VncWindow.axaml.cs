using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MarcusW.VncClient;
using MarcusW.VncClient.Avalonia;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging.Abstractions;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

/// <summary>Embedded VNC viewer. The Avalonia <c>VncView</c> control (MarcusW.VncClient.Avalonia) renders
/// the framebuffer and forwards mouse/keyboard itself – we only open the RFB connection and hand it over.
/// The VNC password (separate from any SSH login) is prompted for when the server asks. The connection is
/// disposed off the UI thread when the window closes.
/// <para>⚠️ Controls are resolved with <see cref="ControlExtensions.FindControl"/>, not x:Name fields: the
/// XAML references an external control (VncView) and the Avalonia source generator doesn't emit the typed
/// fields for this view, so the fields would be null (that was a NullReferenceException in the ctor).</para></summary>
public partial class VncWindow : Window
{
    private readonly string _host = "";
    private readonly int _port;
    private TextBlock? _status;
    private Border? _statusOverlay;
    private VncView? _view;
    private RfbConnection? _connection;

    public VncWindow() => AvaloniaXamlLoader.Load(this);

    public VncWindow(string host, int port) : this()
    {
        _host = host;
        _port = port;
        _status = this.FindControl<TextBlock>("Status");
        _statusOverlay = this.FindControl<Border>("StatusOverlay");
        _view = this.FindControl<VncView>("Vnc");
        Title = $"VNC — {host}:{port}";
        SetStatus(T("Av_VncConnecting"));

        Opened += async (_, _) => await ConnectAsync();
        Closed += (_, _) =>
        {
            var conn = _connection;
            _connection = null;
            Task.Run(() => { try { conn?.Dispose(); } catch { /* already gone */ } });
        };
    }

    private async Task ConnectAsync()
    {
        // No view means the framebuffer has nowhere to render – say so instead of hiding the overlay over a
        // blank pane, which would look exactly like a silent connect failure.
        if (_view is null)
        {
            SetStatus(T("Av_VncFailed", "view control not found"));
            return;
        }
        try
        {
            var client = new VncClient(NullLoggerFactory.Instance);
            var parameters = new ConnectParameters
            {
                TransportParameters = new TcpTransportParameters { Host = _host, Port = _port },
                AuthenticationHandler = new PasswordHandler(this),
            };
            _connection = await client.ConnectAsync(parameters, CancellationToken.None);
            _view.Connection = _connection;

            // ⚠️ Force a FULL framebuffer update now that the view is wired in as the render target. This is
            // the fix for "the VNC pane stays white after the password". ConnectAsync returns once the RFB
            // handshake is done, and the connection can process its first framebuffer update before the line
            // above makes the view its render target – so that initial full frame is drawn to nothing. Every
            // later update is INCREMENTAL (changed regions only), so the pane then stays blank until something
            // moves on the remote screen (the well-known "VncView white until you resize the window"). A
            // non-incremental request over the whole framebuffer makes the server resend the entire screen to
            // the now-attached view.
            var size = _connection.RemoteFramebufferSize;
            if (size.Width > 0 && size.Height > 0)
                _connection.EnqueueFramebufferUpdateRequest(
                    new Rectangle(0, 0, size.Width, size.Height), incremental: false, CancellationToken.None);

            // Scale locally, don't resize the remote: the Viewbox grows the image, and most servers can't
            // change their resolution anyway. Then size the window to the remote screen so it opens at 1:1.
            _view.AutoResizeRemote = false;
            if (size.Width > 0 && size.Height > 0)
                SizeToFramebuffer(size.Width, size.Height);

            // Connected – hide the overlay so the framebuffer is unobstructed.
            if (_statusOverlay is not null) _statusOverlay.IsVisible = false;
        }
        catch (Exception ex)
        {
            // Keep the overlay up with the reason – a failed connect must not look like an empty grey pane.
            SetStatus(T("Av_VncFailed", ex.Message));
        }
    }

    /// <summary>Sizes the window to the remote screen so it opens at 1:1 (no wasted space), capped to 95 % of
    /// the monitor's work area so a large remote desktop can't open a window bigger than the screen. The
    /// Viewbox then scales the framebuffer to whatever size the user drags the window to.
    /// <para>The framebuffer size is in physical pixels; window Width/Height are in device-independent units,
    /// hence the divide by <see cref="Avalonia.Controls.TopLevel.RenderScaling"/>.</para></summary>
    private void SizeToFramebuffer(int pxWidth, int pxHeight)
    {
        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        double wDip = pxWidth / scaling;
        double hDip = pxHeight / scaling;

        var area = Screens.Primary?.WorkingArea;
        if (area is { } a)
        {
            wDip = Math.Min(wDip, a.Width / scaling * 0.95);
            hDip = Math.Min(hDip, a.Height / scaling * 0.95);
        }
        Width = Math.Max(320, wDip);
        Height = Math.Max(240, hDip);

        // Re-centre: WindowStartupLocation only applies when the window opens, before the size was known.
        if (area is { } ar)
        {
            int x = ar.X + (int)((ar.Width - Width * scaling) / 2);
            int y = ar.Y + (int)((ar.Height - Height * scaling) / 2);
            Position = new global::Avalonia.PixelPoint(Math.Max(ar.X, x), Math.Max(ar.Y, y));
        }
    }

    private void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
        if (_statusOverlay is not null) _statusOverlay.IsVisible = true;
    }

    /// <summary>Supplies the VNC password on demand (prompted on the UI thread). The password is used only
    /// to authenticate to the server and is never stored.</summary>
    private sealed class PasswordHandler(VncWindow owner) : IAuthenticationHandler
    {
        public async Task<TInput> ProvideAuthenticationInputAsync<TInput>(RfbConnection connection,
            ISecurityType securityType, IAuthenticationInputRequest<TInput> request)
            where TInput : class, IAuthenticationInput
        {
            if (typeof(TInput) == typeof(PasswordAuthenticationInput))
            {
                var pwd = await Dispatcher.UIThread.InvokeAsync(() =>
                    new PasswordPromptWindow(T("Av_VncPwPrompt", owner._host)).ShowDialog<string?>(owner)) ?? "";
                return (TInput)(object)new PasswordAuthenticationInput(pwd);
            }
            throw new InvalidOperationException("Nicht unterstützte VNC-Authentifizierung: " + typeof(TInput).Name);
        }
    }
}
