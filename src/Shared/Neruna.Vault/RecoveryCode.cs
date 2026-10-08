using System.Text;

namespace Neruna.Vault;

/// <summary>
/// Human-friendly encoding of the 256-bit recovery secret: Crockford Base32 in groups of four,
/// e.g. <c>7K3M-QX9A-…</c>. Decoding ignores case, separators and the usual look-alikes (O→0, I/L→1).
/// </summary>
public static class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int SecretLength = 32;

    public static string Encode(ReadOnlySpan<byte> secret)
    {
        var chars = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in secret)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            chars.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        var grouped = new StringBuilder();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                grouped.Append('-');
            }

            grouped.Append(chars[i]);
        }

        return grouped.ToString();
    }

    /// <exception cref="FormatException">The code contains invalid characters or has the wrong length.</exception>
    public static byte[] Decode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var result = new List<byte>(SecretLength);
        int buffer = 0, bits = 0;

        foreach (var raw in code)
        {
            if (raw is '-' or ' ')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"Invalid character '{raw}' in recovery code.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        if (result.Count != SecretLength)
        {
            throw new FormatException("The recovery code has the wrong length.");
        }

        return [.. result];
    }
}
