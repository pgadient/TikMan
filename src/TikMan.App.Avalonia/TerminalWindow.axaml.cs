using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TikMan.Core.Api;

namespace TikMan.App.Avalonia;

/// <summary>A simple interactive SSH console: it pumps the shell's output into a read-only monospace box
/// (ANSI escape sequences stripped so it stays readable) and sends each entered line to the shell. Not a
/// full xterm – full-screen/cursor apps (vi …) won't render – but line-oriented network CLIs (RouterOS
/// /export & print, Zyxel show …) work fine. The session is disposed when the window closes.</summary>
public partial class TerminalWindow : Window
{
    private ITerminalSession? _session;

    public TerminalWindow() => AvaloniaXamlLoader.Load(this);

    public TerminalWindow(ITerminalSession session, string deviceLabel) : this()
    {
        _session = session;
        Title = $"SSH — {deviceLabel}";
        session.DataReceived += OnData;
        Closed += (_, _) => { session.DataReceived -= OnData; session.Dispose(); };
        Opened += (_, _) => Input.Focus();
    }

    private void OnData(byte[] data)
    {
        var text = Clean(Encoding.UTF8.GetString(data));
        Dispatcher.UIThread.Post(() =>
        {
            Output.Text += text;
            Output.CaretIndex = Output.Text?.Length ?? 0; // keeps the newest output in view
        });
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var line = (Input.Text ?? "") + "\n";
        Input.Text = "";
        var bytes = Encoding.UTF8.GetBytes(line);
        _session?.Write(bytes, bytes.Length);
        e.Handled = true;
    }

    /// <summary>Strips ANSI/VT control so the console stays readable: CSI colour/cursor sequences, the
    /// bracketed-paste toggles, and lone carriage returns (RouterOS redraws its prompt with them).</summary>
    private static string Clean(string s) =>
        Regex.Replace(s, @"\x1b\[[0-9;?]*[A-Za-z]|\x1b[()][A-Z0-9]|\x1b[=>]", "").Replace("\r", "");
}
