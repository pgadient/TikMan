using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Rendering;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging.Abstractions;
using static TikMan.App.Localization.LocalizationManager;
using WpfPixelFormats = System.Windows.Media.PixelFormats;
using VncPixelFormat = MarcusW.VncClient.PixelFormat;
using VncSize = MarcusW.VncClient.Size;

namespace TikMan.App;

/// <summary>Embedded VNC viewer built on MarcusW.VncClient (negotiates RFB 3.3/3.7/3.8). The client
/// renders each framebuffer into an unmanaged buffer we hand it; we blit that into a WPF
/// WriteableBitmap and forward mouse + keyboard. Basic on purpose (a standalone client is more
/// capable – the badge shows an advisory first).</summary>
public partial class VncViewerWindow : Window
{
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly FramebufferTarget _target;
    private RfbConnection? _connection;
    private WriteableBitmap? _bitmap;
    private MouseButtons _buttons = MouseButtons.None;

    // BGRA, 32bpp, little-endian: memory bytes B,G,R,A → matches WPF Bgra32.
    private static readonly VncPixelFormat Bgra32 = new(
        "BGRA32", bitsPerPixel: 32, depth: 24, bigEndian: false, trueColor: true, hasAlpha: true,
        redMax: 255, greenMax: 255, blueMax: 255, alphaMax: 255,
        redShift: 16, greenShift: 8, blueShift: 0, alphaShift: 24);

    // Non-text keys / modifiers → X11 keysyms (KeySymbol's values are X11 keysyms).
    private static readonly Dictionary<Key, int> KeyMap = new()
    {
        [Key.Enter] = 0xFF0D, [Key.Back] = 0xFF08, [Key.Tab] = 0xFF09, [Key.Escape] = 0xFF1B,
        [Key.Delete] = 0xFFFF, [Key.Insert] = 0xFF63, [Key.Home] = 0xFF50, [Key.End] = 0xFF57,
        [Key.PageUp] = 0xFF55, [Key.PageDown] = 0xFF56,
        [Key.Left] = 0xFF51, [Key.Up] = 0xFF52, [Key.Right] = 0xFF53, [Key.Down] = 0xFF54,
        [Key.LeftShift] = 0xFFE1, [Key.RightShift] = 0xFFE2, [Key.LeftCtrl] = 0xFFE3, [Key.RightCtrl] = 0xFFE4,
        [Key.LeftAlt] = 0xFFE9, [Key.RightAlt] = 0xFFEA, [Key.LWin] = 0xFFEB, [Key.RWin] = 0xFFEC,
        [Key.CapsLock] = 0xFFE5,
        [Key.F1] = 0xFFBE, [Key.F2] = 0xFFBF, [Key.F3] = 0xFFC0, [Key.F4] = 0xFFC1, [Key.F5] = 0xFFC2,
        [Key.F6] = 0xFFC3, [Key.F7] = 0xFFC4, [Key.F8] = 0xFFC5, [Key.F9] = 0xFFC6, [Key.F10] = 0xFFC7,
        [Key.F11] = 0xFFC8, [Key.F12] = 0xFFC9,
    };

    public VncViewerWindow(string host, int port)
    {
        InitializeComponent();
        _host = host;
        _port = port;
        Title = $"VNC — {host}:{port}";
        StatusText.Text = T("Vnc_Connecting");
        _target = new FramebufferTarget(this);

        PreviewKeyDown += (_, e) => { if (SendKeyEvent(e.Key, true)) e.Handled = true; };
        PreviewKeyUp += (_, e) => { if (SendKeyEvent(e.Key, false)) e.Handled = true; };
        PreviewTextInput += OnTextInput;

        Loaded += async (_, _) => { Keyboard.Focus(this); await ConnectAsync(); };
        Closed += (_, _) =>
        {
            _cts.Cancel();
            try { _connection?.Dispose(); } catch { /* already gone */ }
            _target.Free();
            Owner?.Activate(); // return focus to the main window (it used to stay unfocusable)
        };
    }

    private async Task ConnectAsync()
    {
        var client = new VncClient(NullLoggerFactory.Instance);
        var parameters = new ConnectParameters
        {
            TransportParameters = new TcpTransportParameters { Host = _host, Port = _port },
            AuthenticationHandler = new PasswordHandler(this),
            InitialRenderTarget = _target,
        };
        try
        {
            _connection = await client.ConnectAsync(parameters, _cts.Token).ConfigureAwait(true);
            StatusText.Text = $"{_host}:{_port}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{T("Vnc_Failed")} {ex.Message}";
        }
    }

    /// <summary>Called by the render target (on the client's thread) once a framebuffer is ready.</summary>
    private void Present(IntPtr address, VncSize size)
    {
        Dispatcher.Invoke(() =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != size.Width || _bitmap.PixelHeight != size.Height)
            {
                _bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, WpfPixelFormats.Bgra32, null);
                Screen.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, size.Width, size.Height), address, size.Width * size.Height * 4, size.Width * 4);
        });
    }

    // ---- mouse ----
    private (int X, int Y)? ToFramebuffer(Point p)
    {
        if (_bitmap is null || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0) return null;
        int x = (int)(p.X * _bitmap.PixelWidth / Screen.ActualWidth);
        int y = (int)(p.Y * _bitmap.PixelHeight / Screen.ActualHeight);
        return (Math.Clamp(x, 0, _bitmap.PixelWidth - 1), Math.Clamp(y, 0, _bitmap.PixelHeight - 1));
    }

    private void Screen_MouseMove(object sender, MouseEventArgs e) => SendPointer(e.GetPosition(Screen), _buttons);

    private void Screen_MouseButton(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);
        _buttons = (Mouse.LeftButton == MouseButtonState.Pressed ? MouseButtons.Left : 0)
                 | (Mouse.MiddleButton == MouseButtonState.Pressed ? MouseButtons.Middle : 0)
                 | (Mouse.RightButton == MouseButtonState.Pressed ? MouseButtons.Right : 0);
        SendPointer(e.GetPosition(Screen), _buttons);
        e.Handled = true;
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var wheel = e.Delta > 0 ? MouseButtons.WheelUp : MouseButtons.WheelDown;
        SendPointer(e.GetPosition(Screen), _buttons | wheel);
        SendPointer(e.GetPosition(Screen), _buttons);
    }

    private void SendPointer(Point p, MouseButtons buttons)
    {
        if (_connection is null || ToFramebuffer(p) is not { } fb) return;
        try { _connection.EnqueueMessage(new PointerEventMessage(new Position(fb.X, fb.Y), buttons), CancellationToken.None); }
        catch { /* connection gone */ }
    }

    // ---- keyboard ----
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var c in e.Text) { SendKey(c, true); SendKey(c, false); }
        e.Handled = true;
    }

    private bool SendKeyEvent(Key key, bool pressed)
    {
        int keysym;
        if (KeyMap.TryGetValue(key, out var mapped)) keysym = mapped;
        else if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 && LetterOrDigit(key) is { } ch)
            keysym = ch;
        else return false; // ordinary character → handled by TextInput
        SendKey(keysym, pressed);
        return true;
    }

    private void SendKey(int keysym, bool pressed)
    {
        if (_connection is null) return;
        try { _connection.EnqueueMessage(new KeyEventMessage(pressed, (KeySymbol)keysym), CancellationToken.None); }
        catch { /* connection gone */ }
    }

    private static int? LetterOrDigit(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 'a' + (key - Key.A),
        >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
        _ => null,
    };

    /// <summary>Render target: hands the client an unmanaged BGRA buffer, then blits it to the bitmap.</summary>
    private sealed class FramebufferTarget : IRenderTarget
    {
        private readonly VncViewerWindow _owner;
        private IntPtr _buffer;
        private int _length;

        public FramebufferTarget(VncViewerWindow owner) => _owner = owner;

        public IFramebufferReference GrabFramebufferReference(VncSize size, IImmutableSet<Screen> layout)
        {
            int needed = size.Width * size.Height * 4;
            if (needed > _length)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal(needed);
                _length = needed;
            }
            return new Reference(_owner, _buffer, size);
        }

        public void Free()
        {
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _length = 0; }
        }

        private sealed class Reference(VncViewerWindow owner, IntPtr address, VncSize size) : IFramebufferReference
        {
            public IntPtr Address => address;
            public VncSize Size => size;
            public VncPixelFormat Format => Bgra32;
            public double HorizontalDpi => 96;
            public double VerticalDpi => 96;
            // The client has finished writing into the buffer → present it (blocks this thread briefly).
            public void Dispose() => owner.Present(address, size);
        }
    }

    /// <summary>Supplies the VNC password on demand (prompted on the UI thread).</summary>
    private sealed class PasswordHandler(VncViewerWindow owner) : IAuthenticationHandler
    {
        public Task<TInput> ProvideAuthenticationInputAsync<TInput>(RfbConnection connection,
            ISecurityType securityType, IAuthenticationInputRequest<TInput> request)
            where TInput : class, IAuthenticationInput
        {
            if (typeof(TInput) == typeof(PasswordAuthenticationInput))
            {
                var pwd = owner.Dispatcher.Invoke(() => PasswordPrompt.Show(owner, owner._host)) ?? "";
                return Task.FromResult((TInput)(object)new PasswordAuthenticationInput(pwd));
            }
            throw new InvalidOperationException($"Unsupported VNC authentication input: {typeof(TInput).Name}");
        }
    }
}
