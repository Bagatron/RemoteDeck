namespace RemoteDeck.Protocols.Vnc;

/// <summary>
/// Single DES, which the classic VNC password check needs. The framework's own DES refuses weak keys, and a VNC
/// password can turn into one, so this is a plain implementation of the standard (FIPS 46-3). Only the encrypt
/// direction is needed. It is used for nothing else.
/// </summary>
internal static class Des
{
    private static readonly byte[] InitialPermutation =
    {
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7,
    };

    private static readonly byte[] FinalPermutation =
    {
        40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
        38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
        36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
        34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25,
    };

    private static readonly byte[] Expansion =
    {
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11,
        12, 13, 12, 13, 14, 15, 16, 17, 16, 17, 18, 19, 20, 21, 20, 21,
        22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1,
    };

    private static readonly byte[] Permutation =
    {
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
        2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25,
    };

    private static readonly byte[] PermutedChoice1 =
    {
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2,
        59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36, 63, 55, 47, 39,
        31, 23, 15, 7, 62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37,
        29, 21, 13, 5, 28, 20, 12, 4,
    };

    private static readonly byte[] PermutedChoice2 =
    {
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4,
        26, 8, 16, 7, 27, 20, 13, 2, 41, 52, 31, 37, 47, 55, 30, 40,
        51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
    };

    private static readonly int[] Shifts = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };

    private static readonly byte[][] SBoxes =
    {
        new byte[]
        {
            14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
            0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
            4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
            15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13,
        },
        new byte[]
        {
            15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
            3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
            0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
            13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9,
        },
        new byte[]
        {
            10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
            13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
            13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
            1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12,
        },
        new byte[]
        {
            7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
            13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
            10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
            3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14,
        },
        new byte[]
        {
            2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
            14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
            4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
            11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3,
        },
        new byte[]
        {
            12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
            10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
            9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
            4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13,
        },
        new byte[]
        {
            4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
            13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
            1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
            6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12,
        },
        new byte[]
        {
            13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
            1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
            7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
            2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11,
        },
    };

    /// <summary>Encrypts one 8-byte block with an 8-byte key.</summary>
    public static byte[] EncryptBlock(byte[] key, byte[] block)
    {
        if (key.Length != 8 || block.Length != 8)
        {
            throw new ArgumentException("DES works on 8-byte keys and blocks.");
        }

        var subKeys = MakeSubKeys(key);
        var bits = Permute(ToBits(block), InitialPermutation);
        var left = bits[..32];
        var right = bits[32..];

        for (var round = 0; round < 16; round++)
        {
            var expanded = Permute(right, Expansion);
            var mixed = new int[48];
            for (var i = 0; i < 48; i++)
            {
                mixed[i] = expanded[i] ^ subKeys[round][i];
            }

            var substituted = new int[32];
            for (var box = 0; box < 8; box++)
            {
                var offset = box * 6;
                var row = (mixed[offset] << 1) | mixed[offset + 5];
                var column = (mixed[offset + 1] << 3) | (mixed[offset + 2] << 2) | (mixed[offset + 3] << 1) | mixed[offset + 4];
                var value = SBoxes[box][(row * 16) + column];
                for (var bit = 0; bit < 4; bit++)
                {
                    substituted[(box * 4) + bit] = (value >> (3 - bit)) & 1;
                }
            }

            var permuted = Permute(substituted, Permutation);
            var next = new int[32];
            for (var i = 0; i < 32; i++)
            {
                next[i] = left[i] ^ permuted[i];
            }

            left = right;
            right = next;
        }

        var joined = new int[64];
        right.CopyTo(joined, 0);
        left.CopyTo(joined, 32);
        return ToBytes(Permute(joined, FinalPermutation));
    }

    private static int[][] MakeSubKeys(byte[] key)
    {
        var permuted = Permute(ToBits(key), PermutedChoice1);
        var c = permuted[..28];
        var d = permuted[28..];
        var keys = new int[16][];
        for (var round = 0; round < 16; round++)
        {
            c = Rotate(c, Shifts[round]);
            d = Rotate(d, Shifts[round]);
            var joined = new int[56];
            c.CopyTo(joined, 0);
            d.CopyTo(joined, 28);
            keys[round] = Permute(joined, PermutedChoice2);
        }

        return keys;
    }

    private static int[] Rotate(int[] bits, int count)
    {
        var result = new int[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            result[i] = bits[(i + count) % bits.Length];
        }

        return result;
    }

    private static int[] Permute(int[] bits, byte[] table)
    {
        var result = new int[table.Length];
        for (var i = 0; i < table.Length; i++)
        {
            result[i] = bits[table[i] - 1];
        }

        return result;
    }

    private static int[] ToBits(byte[] bytes)
    {
        var bits = new int[bytes.Length * 8];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = (bytes[i / 8] >> (7 - (i % 8))) & 1;
        }

        return bits;
    }

    private static byte[] ToBytes(int[] bits)
    {
        var bytes = new byte[bits.Length / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            bytes[i / 8] |= (byte)(bits[i] << (7 - (i % 8)));
        }

        return bytes;
    }
}
