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
        Opened += (_, _) => InputBox?.Focus();
    }

    /// <summary>⚠️ Looked up by name, never through the <c>x:Name</c> field.
    ///
    /// <para>Whether those fields are populated depends on whether <b>this particular view's</b> XAML was
    /// compiled: the compiled populate method assigns them, the runtime parser does not – it builds the
    /// tree and the name scope and leaves every field null. Both paths run the same C#, so the difference
    /// is invisible until a handler dereferences one. That is what killed the process when a VNC password
    /// was confirmed. Relying on which views happen to compile is not a property worth depending on.</para></summary>
    private TextBox? InputBox => this.FindControl<TextBox>("Input");
    private TextBox? OutputBox => this.FindControl<TextBox>("Output");

    // The console so far, kept as completed lines plus the line currently being drawn. Held here (not read
    // back off the TextBox) so carriage-return redraws can be applied – see Append.
    private readonly StringBuilder _committed = new();
    private readonly StringBuilder _line = new();
    private bool _pendingCr; // a chunk ended on a bare '\r'; whether it's CRLF or a redraw depends on the next
    private const int MaxChars = 400_000; // cap so a chatty session doesn't grow without bound

    private void OnData(byte[] data)
    {
        var text = Clean(Encoding.UTF8.GetString(data));
        Dispatcher.UIThread.Post(() =>
        {
            Append(text);
            if (OutputBox is not { } box) return;
            box.Text = _committed.ToString() + _line;
            box.CaretIndex = box.Text.Length; // keeps the newest output in view
        });
    }

    /// <summary>Applies a terminal's carriage-return / newline behaviour to the incoming text.
    /// <para>⚠️ This is the fix for the prompt showing up twice on one line ("HOST&gt; HOST&gt;"). A device
    /// redraws its prompt with a bare CR (return to column 0, next characters overwrite); the old code just
    /// deleted every <c>\r</c>, so the redraw piled up next to the old prompt instead of replacing it. Here a
    /// bare CR clears the current line so the redraw overwrites it, while CRLF and a lone LF both end the
    /// line.</para></summary>
    private void Append(string text)
    {
        var i = 0;
        // Resolve a '\r' that ended the previous chunk now that the next character is known: '\n' after it
        // makes it a CRLF (one newline), anything else makes it a bare-CR redraw (overwrite the line).
        if (_pendingCr)
        {
            _pendingCr = false;
            if (text.Length > 0 && text[0] == '\n') { CommitLine(); i = 1; }
            else _line.Clear();
        }
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length)
                {
                    if (text[i + 1] == '\n') { CommitLine(); i++; } // CRLF = one newline
                    else _line.Clear();                             // bare CR = overwrite
                }
                else _pendingCr = true;                             // CR at the very end: decide next chunk
            }
            else if (c == '\n') CommitLine();
            else _line.Append(c);
        }
    }

    private void CommitLine()
    {
        _committed.Append(_line).Append('\n');
        _line.Clear();
        if (_committed.Length > MaxChars)
        {
            // Trim from the front on a line boundary, so nothing reads as cut mid-word.
            var s = _committed.ToString();
            var cut = s.IndexOf('\n', s.Length - MaxChars);
            if (cut >= 0) { _committed.Clear(); _committed.Append(s, cut + 1, s.Length - cut - 1); }
        }
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || InputBox is not { } input) return;
        var line = (input.Text ?? "") + "\n";
        input.Text = "";
        var bytes = Encoding.UTF8.GetBytes(line);
        _session?.Write(bytes, bytes.Length);
        e.Handled = true;
    }

    /// <summary>Strips ANSI/VT control so the console stays readable: CSI colour/cursor sequences and the
    /// charset/bracketed-paste toggles. ⚠️ Carriage returns are deliberately KEPT now – <see cref="Append"/>
    /// applies their overwrite semantics, which is what stops a redrawn prompt from doubling up.</summary>
    private static string Clean(string s) =>
        Regex.Replace(s, @"\x1b\[[0-9;?]*[A-Za-z]|\x1b[()][A-Z0-9]|\x1b[=>]", "");
}
