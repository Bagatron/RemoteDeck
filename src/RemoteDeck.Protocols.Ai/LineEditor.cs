using System.Text;

namespace RemoteDeck.Protocols.Ai;

/// <summary>
/// A tiny line editor for the chat prompt. Feed it the bytes the terminal sends; it returns what to echo back and
/// the lines the user finished with Enter. Handles typing, Backspace, Ctrl+U, Ctrl+C and pasted text.
/// </summary>
internal sealed class LineEditor
{
    private readonly StringBuilder _line = new();
    private readonly List<byte> _pending = new();
    private bool _lastWasCr;

    public string Current => _line.ToString();

    /// <summary>Processes input. Returns the text to echo to the terminal; completed lines are appended to <paramref name="lines"/>.</summary>
    public string Feed(ReadOnlySpan<byte> input, List<string> lines)
    {
        _pending.AddRange(input.ToArray());
        var text = DecodePending();
        var echo = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\u001b')
            {
                // Skip an escape sequence (arrow keys etc.): ESC [ ... final byte, or ESC O x.
                if (i + 1 < text.Length && (text[i + 1] == '[' || text[i + 1] == 'O'))
                {
                    i += 2;
                    while (i < text.Length && !(text[i] >= '@' && text[i] <= '~'))
                    {
                        i++;
                    }
                }

                continue;
            }

            if (c == '\n' && _lastWasCr)
            {
                _lastWasCr = false;
                continue;
            }

            _lastWasCr = c == '\r';
            if (c is '\r' or '\n')
            {
                echo.Append("\r\n");
                lines.Add(_line.ToString());
                _line.Clear();
            }
            else if (c is '\u007f' or '\b')
            {
                if (_line.Length > 0)
                {
                    var width = char.IsLowSurrogate(_line[^1]) && _line.Length > 1 ? 2 : 1;
                    _line.Length -= width;
                    echo.Append("\b \b");
                }
            }
            else if (c == '\u0015')
            {
                for (var n = _line.Length; n > 0; n--)
                {
                    echo.Append("\b \b");
                }

                _line.Clear();
            }
            else if (c == '\u0003')
            {
                echo.Append("^C\r\n");
                _line.Clear();
                lines.Add("\u0003");
            }
            else if (!char.IsControl(c))
            {
                _line.Append(c);
                echo.Append(c);
            }
        }

        return echo.ToString();
    }

    private string DecodePending()
    {
        var bytes = _pending.ToArray();
        // Keep an incomplete UTF-8 tail for the next call.
        var keep = 0;
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80)
            {
                continue;
            }

            var need = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;
            if (need > back)
            {
                keep = back;
            }

            break;
        }

        _pending.Clear();
        if (keep > 0)
        {
            _pending.AddRange(bytes[^keep..]);
        }

        return Encoding.UTF8.GetString(bytes, 0, bytes.Length - keep);
    }
}
