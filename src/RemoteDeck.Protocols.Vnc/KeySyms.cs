namespace RemoteDeck.Protocols.Vnc;

/// <summary>X11 key symbols, which is what VNC sends for a key.</summary>
public static class KeySyms
{
    public const uint BackSpace = 0xFF08;
    public const uint Tab = 0xFF09;
    public const uint Return = 0xFF0D;
    public const uint Escape = 0xFF1B;
    public const uint Insert = 0xFF63;
    public const uint Delete = 0xFFFF;
    public const uint Home = 0xFF50;
    public const uint Left = 0xFF51;
    public const uint Up = 0xFF52;
    public const uint Right = 0xFF53;
    public const uint Down = 0xFF54;
    public const uint PageUp = 0xFF55;
    public const uint PageDown = 0xFF56;
    public const uint End = 0xFF57;
    public const uint F1 = 0xFFBE;
    public const uint ShiftLeft = 0xFFE1;
    public const uint ShiftRight = 0xFFE2;
    public const uint ControlLeft = 0xFFE3;
    public const uint ControlRight = 0xFFE4;
    public const uint CapsLock = 0xFFE5;
    public const uint AltLeft = 0xFFE9;
    public const uint AltRight = 0xFFEA;
    public const uint SuperLeft = 0xFFEB;
    public const uint SuperRight = 0xFFEC;

    /// <summary>The key symbol for function key <paramref name="number"/> (1 to 12).</summary>
    public static uint Function(int number) => F1 + (uint)(number - 1);

    /// <summary>
    /// The key symbol for a typed character. Latin-1 characters are their own code; anything else is the Unicode
    /// code point offset by 0x01000000, which is how X11 and VNC servers name them.
    /// </summary>
    public static uint FromCodePoint(int codePoint)
    {
        if (codePoint is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF)
        {
            return (uint)codePoint;
        }

        return 0x01000000u + (uint)codePoint;
    }
}
