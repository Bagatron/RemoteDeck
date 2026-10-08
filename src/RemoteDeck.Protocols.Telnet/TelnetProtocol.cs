namespace RemoteDeck.Protocols.Telnet;

/// <summary>
/// The Telnet wire format (RFC 854 and friends), without any networking. It takes the bytes a server sent, pulls the
/// negotiation out of them, and hands back what to show in the terminal and what to answer.
///
/// It agrees to the options a terminal needs: the server echoing (ECHO), no go-ahead (SGA), the terminal type
/// (TTYPE) and the window size (NAWS). It refuses everything else.
/// </summary>
internal sealed class TelnetParser
{
    public const byte Iac = 255;
    public const byte Dont = 254;
    public const byte Do = 253;
    public const byte Wont = 252;
    public const byte Will = 251;
    public const byte Sb = 250;
    public const byte Se = 240;

    public const byte OptEcho = 1;
    public const byte OptSuppressGoAhead = 3;
    public const byte OptTerminalType = 24;
    public const byte OptWindowSize = 31;

    private const int MaxSubnegotiation = 1024;

    private enum Mode
    {
        Data,
        Iac,
        Command,
        Sub,
        SubIac,
    }

    private readonly string _terminalType;
    private readonly bool[] _weEnabled = new bool[256];
    private readonly bool[] _theyEnabled = new bool[256];
    private readonly List<byte> _subnegotiation = new();

    private Mode _mode = Mode.Data;
    private byte _command;
    private bool _lastWasCr;
    private int _columns = 80;
    private int _rows = 24;

    public TelnetParser(string terminalType)
    {
        _terminalType = string.IsNullOrWhiteSpace(terminalType) ? "xterm-256color" : terminalType;
    }

    /// <summary>True once the server has asked for the window size and we agreed.</summary>
    public bool SendsWindowSize => _weEnabled[OptWindowSize];

    /// <summary>True while the server has said it echoes what you type.</summary>
    public bool ServerEchoes => _theyEnabled[OptEcho];

    /// <summary>Remembers the terminal size. Returns the frame to send when the server wants size updates, else empty.</summary>
    public byte[] SetSize(int columns, int rows)
    {
        _columns = Math.Clamp(columns, 1, 65535);
        _rows = Math.Clamp(rows, 1, 65535);
        return SendsWindowSize ? WindowSizeFrame() : Array.Empty<byte>();
    }

    /// <summary>Splits what the server sent into text for the terminal and answers for the server.</summary>
    public (byte[] Output, byte[] Reply) Feed(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        var reply = new List<byte>();

        foreach (var b in input)
        {
            switch (_mode)
            {
                case Mode.Data:
                    if (b == Iac)
                    {
                        _mode = Mode.Iac;
                    }
                    else if (_lastWasCr && b == 0)
                    {
                        // CR NUL is how Telnet writes a bare carriage return; the NUL is not text.
                        _lastWasCr = false;
                    }
                    else
                    {
                        output.Add(b);
                        _lastWasCr = b == 13;
                    }

                    break;

                case Mode.Iac:
                    if (b is Will or Wont or Do or Dont)
                    {
                        _command = b;
                        _mode = Mode.Command;
                    }
                    else if (b == Sb)
                    {
                        _subnegotiation.Clear();
                        _mode = Mode.Sub;
                    }
                    else
                    {
                        if (b == Iac)
                        {
                            // An escaped 255 that is really data.
                            output.Add(Iac);
                            _lastWasCr = false;
                        }

                        // Other commands (NOP, Go Ahead, ...) need no answer.
                        _mode = Mode.Data;
                    }

                    break;

                case Mode.Command:
                    Negotiate(_command, b, reply);
                    _mode = Mode.Data;
                    break;

                case Mode.Sub:
                    if (b == Iac)
                    {
                        _mode = Mode.SubIac;
                    }
                    else if (_subnegotiation.Count < MaxSubnegotiation)
                    {
                        _subnegotiation.Add(b);
                    }

                    break;

                case Mode.SubIac:
                    if (b == Se)
                    {
                        Subnegotiate(reply);
                        _mode = Mode.Data;
                    }
                    else
                    {
                        if (b == Iac && _subnegotiation.Count < MaxSubnegotiation)
                        {
                            _subnegotiation.Add(Iac);
                        }

                        _mode = Mode.Sub;
                    }

                    break;
            }
        }

        return (output.ToArray(), reply.ToArray());
    }

    /// <summary>
    /// Turns keystrokes into what goes on the wire: 255 is doubled, and a Return that is not followed by a line feed
    /// becomes CR NUL (the Telnet way to write a bare carriage return).
    /// </summary>
    public static byte[] EncodeInput(ReadOnlySpan<byte> input)
    {
        var result = new List<byte>(input.Length + 2);
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            result.Add(b);
            if (b == Iac)
            {
                result.Add(Iac);
            }
            else if (b == 13 && (i + 1 >= input.Length || input[i + 1] != 10))
            {
                result.Add(0);
            }
        }

        return result.ToArray();
    }

    private void Negotiate(byte command, byte option, List<byte> reply)
    {
        switch (command)
        {
            case Do:
                if (option is OptTerminalType or OptWindowSize)
                {
                    if (!_weEnabled[option])
                    {
                        _weEnabled[option] = true;
                        reply.AddRange(new[] { Iac, Will, option });
                        if (option == OptWindowSize)
                        {
                            reply.AddRange(WindowSizeFrame());
                        }
                    }
                }
                else
                {
                    reply.AddRange(new[] { Iac, Wont, option });
                }

                break;

            case Dont:
                if (_weEnabled[option])
                {
                    _weEnabled[option] = false;
                    reply.AddRange(new[] { Iac, Wont, option });
                }

                break;

            case Will:
                if (option is OptEcho or OptSuppressGoAhead)
                {
                    if (!_theyEnabled[option])
                    {
                        _theyEnabled[option] = true;
                        reply.AddRange(new[] { Iac, Do, option });
                    }
                }
                else
                {
                    reply.AddRange(new[] { Iac, Dont, option });
                }

                break;

            case Wont:
                if (_theyEnabled[option])
                {
                    _theyEnabled[option] = false;
                    reply.AddRange(new[] { Iac, Dont, option });
                }

                break;
        }
    }

    private void Subnegotiate(List<byte> reply)
    {
        // The server asks for the terminal type: IAC SB TTYPE SEND IAC SE.
        if (_subnegotiation.Count >= 2 && _subnegotiation[0] == OptTerminalType && _subnegotiation[1] == 1 && _weEnabled[OptTerminalType])
        {
            reply.AddRange(new[] { Iac, Sb, OptTerminalType, (byte)0 });
            foreach (var c in _terminalType)
            {
                reply.Add((byte)(c < 128 ? c : '?'));
            }

            reply.AddRange(new[] { Iac, Se });
        }
    }

    private byte[] WindowSizeFrame()
    {
        var frame = new List<byte> { Iac, Sb, OptWindowSize };
        foreach (var value in new[] { _columns >> 8, _columns & 0xFF, _rows >> 8, _rows & 0xFF })
        {
            frame.Add((byte)value);
            if (value == Iac)
            {
                frame.Add(Iac);
            }
        }

        frame.AddRange(new[] { Iac, Se });
        return frame.ToArray();
    }
}
