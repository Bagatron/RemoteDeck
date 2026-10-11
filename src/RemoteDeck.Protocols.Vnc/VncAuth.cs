using System.Text;

namespace RemoteDeck.Protocols.Vnc;

/// <summary>The classic VNC password check: the server sends 16 random bytes and the client returns them encrypted with the password.</summary>
internal static class VncAuth
{
    /// <summary>
    /// Encrypts the 16-byte challenge with DES, using the first eight characters of the password as the key.
    /// VNC's quirk is that every key byte has its bits in reverse order.
    /// </summary>
    public static byte[] Response(byte[] challenge, string password)
    {
        if (challenge.Length != 16)
        {
            throw new ArgumentException("The VNC challenge is 16 bytes.", nameof(challenge));
        }

        var key = new byte[8];
        var raw = Encoding.Latin1.GetBytes(password);
        for (var i = 0; i < 8 && i < raw.Length; i++)
        {
            key[i] = ReverseBits(raw[i]);
        }

        var response = new byte[16];
        for (var block = 0; block < 2; block++)
        {
            var encrypted = Des.EncryptBlock(key, challenge[(block * 8)..((block * 8) + 8)]);
            encrypted.CopyTo(response, block * 8);
        }

        return response;
    }

    private static byte ReverseBits(byte value)
    {
        var result = 0;
        for (var i = 0; i < 8; i++)
        {
            result = (result << 1) | ((value >> i) & 1);
        }

        return (byte)result;
    }
}
