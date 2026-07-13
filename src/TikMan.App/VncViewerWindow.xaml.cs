using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteViewing.Vnc;
using static TikMan.App.Localization.LocalizationManager;

namespace TikMan.App;

/// <summary>A minimal embedded VNC viewer: connects with RemoteViewing's RFB client, renders the
/// framebuffer into a WriteableBitmap and forwards mouse + keyboard. Basic on purpose – a dedicated
/// standalone client is more capable (the badge shows an advisory before opening this).</summary>
public partial class VncViewerWindow : Window
{
    private readonly string _host;
    private readonly int _port;
    private readonly VncClient _client = new();
    private WriteableBitmap? _bitmap;
    private volatile bool _renderQueued;
    private int _buttonMask;

    // Special keys / modifiers → X11 keysyms (printable characters go through TextInput instead).
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

        _client.Connected += (_, _) => Dispatcher.Invoke(() => StatusText.Text = $"{host}:{port}");
        _client.ConnectionFailed += (_, _) => Dispatcher.Invoke(() => StatusText.Text = T("Vnc_Failed"));
        _client.Closed += (_, _) => Dispatcher.Invoke(() => StatusText.Text = T("Vnc_Closed"));
        _client.FramebufferChanged += (_, _) => QueueRender();

        PreviewKeyDown += (_, e) => { if (SendKeyEvent(e.Key, true)) e.Handled = true; };
        PreviewKeyUp += (_, e) => { if (SendKeyEvent(e.Key, false)) e.Handled = true; };
        PreviewTextInput += OnTextInput;

        Loaded += (_, _) => { Keyboard.Focus(this); Connect(); };
        Closed += (_, _) => { try { _client.Close(); } catch { /* already gone */ } };
    }

    private void Connect()
    {
        var options = new VncClientConnectOptions
        {
            // The server asks for a password on demand; prompt on the UI thread and hand it back.
            PasswordRequiredCallback = _ => Dispatcher.Invoke(() => (PasswordPrompt.Show(this, _host) ?? "").ToCharArray()),
        };
        Task.Run(() =>
        {
            try { _client.Connect(_host, _port, options); }
            catch (Exception ex) { Dispatcher.Invoke(() => StatusText.Text = $"{T("Vnc_Failed")} {ex.Message}"); }
        });
    }

    // ---- rendering ----
    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(() => { _renderQueued = false; Render(); });
    }

    private void Render()
    {
        var fb = _client.Framebuffer;
        if (fb is null) return;
        lock (fb.SyncRoot)
        {
            if (_bitmap is null || _bitmap.PixelWidth != fb.Width || _bitmap.PixelHeight != fb.Height)
            {
                _bitmap = new WriteableBitmap(fb.Width, fb.Height, 96, 96, PixelFormats.Bgr32, null);
                Screen.Source = _bitmap;
            }
            var buffer = fb.GetBuffer();
            _bitmap.WritePixels(new Int32Rect(0, 0, fb.Width, fb.Height), buffer, fb.Stride, 0);
        }
    }

    // ---- mouse ----
    private (int X, int Y)? ToFramebuffer(Point p)
    {
        if (_bitmap is null || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0) return null;
        int x = (int)(p.X * _bitmap.PixelWidth / Screen.ActualWidth);
        int y = (int)(p.Y * _bitmap.PixelHeight / Screen.ActualHeight);
        return (Math.Clamp(x, 0, _bitmap.PixelWidth - 1), Math.Clamp(y, 0, _bitmap.PixelHeight - 1));
    }

    private void Screen_MouseMove(object sender, MouseEventArgs e) => SendPointer(e.GetPosition(Screen));

    private void Screen_MouseButton(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);
        _buttonMask = (Mouse.LeftButton == MouseButtonState.Pressed ? 1 : 0)
                    | (Mouse.MiddleButton == MouseButtonState.Pressed ? 2 : 0)
                    | (Mouse.RightButton == MouseButtonState.Pressed ? 4 : 0);
        SendPointer(e.GetPosition(Screen));
        e.Handled = true;
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ToFramebuffer(e.GetPosition(Screen)) is not { } fb) return;
        int wheel = e.Delta > 0 ? 8 : 16; // buttons 4/5 = wheel up/down
        try { _client.SendPointerEvent(fb.X, fb.Y, _buttonMask | wheel); _client.SendPointerEvent(fb.X, fb.Y, _buttonMask); }
        catch { /* not connected */ }
    }

    private void SendPointer(Point p)
    {
        if (ToFramebuffer(p) is not { } fb) return;
        try { _client.SendPointerEvent(fb.X, fb.Y, _buttonMask); } catch { /* not connected */ }
    }

    // ---- keyboard ----
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var c in e.Text)
        {
            try { _client.SendKeyEvent((KeySym)c, true); _client.SendKeyEvent((KeySym)c, false); }
            catch { /* not connected */ }
        }
        e.Handled = true;
    }

    /// <summary>Sends a non-text key (special key, modifier, or a Ctrl/Alt-modified letter). Returns
    /// true when it handled the key so plain typing still flows through TextInput.</summary>
    private bool SendKeyEvent(Key key, bool pressed)
    {
        int keysym;
        if (KeyMap.TryGetValue(key, out var mapped)) keysym = mapped;
        else if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 && LetterOrDigit(key) is { } ch)
            keysym = ch;
        else return false; // ordinary character → handled by TextInput

        try { _client.SendKeyEvent((KeySym)keysym, pressed); } catch { /* not connected */ }
        return true;
    }

    private static int? LetterOrDigit(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 'a' + (key - Key.A),
        >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
        _ => null,
    };
}
