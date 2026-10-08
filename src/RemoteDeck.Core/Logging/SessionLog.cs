using System.Globalization;
using System.Text;

namespace RemoteDeck.Core.Logging;

/// <summary>
/// Writes a terminal session's output to a plain text file. Escape sequences (colors, cursor movement, window
/// titles) are removed, and the text is kept as a person would have seen it: backspaces and carriage returns
/// overwrite, so a progress bar becomes one line and a corrected command shows only its final text.
///
/// What you type is not logged, only what the server sends back. Anything the session prints, including secrets
/// shown on screen, ends up in the file, so keep logs somewhere you trust.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private const int MaxLine = 16 * 1024;

    private enum State
    {
        Text,
        Escape,
        EscapeIntermediate,
        Csi,
        String,
        StringEscape,
    }

    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly List<char> _line = new();
    private readonly StringBuilder _csi = new();
    private int _cursor;
    private State _state = State.Text;
    private bool _disposed;

    private SessionLog(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>The file being written.</summary>
    public string Path { get; }

    /// <summary>Creates a new log file in <paramref name="directory"/>, named after the connection and the time.</summary>
    public static SessionLog Open(string directory, string connectionName, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);

        var baseName = $"{SafeName(connectionName)}_{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        for (var attempt = 0; ; attempt++)
        {
            var path = System.IO.Path.Combine(directory, attempt == 0 ? baseName + ".log" : $"{baseName}-{attempt}.log");
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                writer.WriteLine($"# RemoteDeck session log: {connectionName}, started {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
                return new SessionLog(path, writer);
            }
            catch (IOException) when (File.Exists(path) && attempt < 100)
            {
                // Another log with this name already exists; try the next number.
            }
        }
    }

    /// <summary>A file-name-safe version of a connection name.</summary>
    public static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60];
        }

        return cleaned.Length == 0 ? "session" : cleaned;
    }

    /// <summary>Adds bytes received from the server. Safe to call from any thread; chunks may split a character or a sequence.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var chars = new char[_decoder.GetCharCount(data, flush: false)];
            var count = _decoder.GetChars(data, chars, flush: false);
            for (var i = 0; i < count; i++)
            {
                Accept(chars[i]);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FlushLine(force: true);
            _writer.Dispose();
        }
    }

    private void Accept(char c)
    {
        switch (_state)
        {
            case State.Text:
                AcceptText(c);
                break;

            case State.Escape:
                if (c == '[')
                {
                    _csi.Clear();
                    _state = State.Csi;
                }
                else if (c is ']' or 'P' or 'X' or '^' or '_')
                {
                    _state = State.String;
                }
                else if (c is >= ' ' and <= '/')
                {
                    _state = State.EscapeIntermediate;
                }
                else
                {
                    _state = State.Text;
                }

                break;

            case State.EscapeIntermediate:
                if (c is < ' ' or > '/')
                {
                    _state = State.Text;
                }

                break;

            case State.Csi:
                if (c is >= '@' and <= '~')
                {
                    EndCsi(c);
                    _state = State.Text;
                }
                else
                {
                    _csi.Append(c);
                }

                break;

            case State.String:
                if (c == '\u0007')
                {
                    _state = State.Text;
                }
                else if (c == '\u001b')
                {
                    _state = State.StringEscape;
                }

                break;

            case State.StringEscape:
                _state = c == '\\' ? State.Text : State.String;
                break;
        }
    }

    private void AcceptText(char c)
    {
        switch (c)
        {
            case '\u001b':
                _state = State.Escape;
                break;
            case '\n':
                FlushLine(force: false);
                break;
            case '\r':
                _cursor = 0;
                break;
            case '\b':
                _cursor = Math.Max(0, _cursor - 1);
                break;
            case '\t':
                Put('\t');
                break;
            default:
                if (!char.IsControl(c))
                {
                    Put(c);
                }

                break;
        }
    }

    private void Put(char c)
    {
        if (_cursor < _line.Count)
        {
            _line[_cursor] = c;
        }
        else
        {
            _line.Add(c);
        }

        _cursor++;
        if (_line.Count >= MaxLine)
        {
            FlushLine(force: false);
        }
    }

    private void EndCsi(char final)
    {
        // "Erase to end of line" is how shells redraw a line after an edit.
        if (final == 'K' && (_csi.Length == 0 || _csi.ToString() == "0") && _cursor < _line.Count)
        {
            _line.RemoveRange(_cursor, _line.Count - _cursor);
        }

        _csi.Clear();
    }

    private void FlushLine(bool force)
    {
        if (_line.Count > 0 || !force)
        {
            var text = new string(_line.ToArray()).TrimEnd(' ', '\t');
            if (force && text.Length == 0)
            {
                _line.Clear();
                _cursor = 0;
                return;
            }

            _writer.WriteLine(text);
        }

        _line.Clear();
        _cursor = 0;
    }
}
