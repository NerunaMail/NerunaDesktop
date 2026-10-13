using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Neruna.Vault;

/// <summary>
/// An own account (with its password) from the desktop to the phone as a QR code, without Neruna Cloud: the data is
/// compressed and encrypted (AES-256-GCM) with a key from a six-digit PIN shown next to the code (PBKDF2-SHA256,
/// 600 000 rounds) – a photo of the code alone opens nothing. Compact binary in base64url.
/// </summary>
public static class AccountTransfer
{
    /// <summary>Start of the QR code text.</summary>
    public const string Prefix = "neruna-account:1:";

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int Iterations = 600_000;
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("neruna-account-transfer-v1");

    public static bool IsTransfer(string? text) => text?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>A six-digit PIN for <see cref="Seal"/>.</summary>
    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The QR code text for <paramref name="payload"/>, readable with <paramref name="pin"/>.</summary>
    public static string Seal(byte[] payload, string pin)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(pin);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var plain = Compress(payload);
        var data = new byte[SaltSize + NonceSize + plain.Length + TagSize];
        salt.CopyTo(data, 0);
        var nonce = data.AsSpan(SaltSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(Key(pin, salt), TagSize))
        {
            aes.Encrypt(nonce, plain, data.AsSpan(SaltSize + NonceSize, plain.Length), data.AsSpan(SaltSize + NonceSize + plain.Length), Context);
        }

        return Prefix + Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The payload of the code.</summary>
    /// <exception cref="CryptographicException">Wrong PIN, changed or not a transfer code.</exception>
    public static byte[] Open(string text, string pin)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        if (!IsTransfer(text))
        {
            throw new CryptographicException("Not an account transfer code.");
        }

        byte[] data;
        try
        {
            var body = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            data = Convert.FromBase64String(body.PadRight(body.Length + ((4 - (body.Length % 4)) % 4), '='));
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Damaged transfer code.", ex);
        }

        if (data.Length < SaltSize + NonceSize + TagSize)
        {
            throw new CryptographicException("Damaged transfer code.");
        }

        var plain = new byte[data.Length - SaltSize - NonceSize - TagSize];
        using (var aes = new AesGcm(Key(pin.Trim(), data[..SaltSize]), TagSize))
        {
            aes.Decrypt(data.AsSpan(SaltSize, NonceSize), data.AsSpan(SaltSize + NonceSize, plain.Length), data.AsSpan(SaltSize + NonceSize + plain.Length), plain, Context);
        }

        using var input = new DeflateStream(new MemoryStream(plain), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Key(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }
}
