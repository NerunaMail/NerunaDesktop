using MimeKit;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Neruna.Core.Security;

/// <summary>
/// The Autocrypt header (autocrypt.org): mail programs send their public key along with every mail, so the reply can
/// be encrypted without exchanging keys first. Such keys count as known, not confirmed.
/// </summary>
public static class Autocrypt
{
    public const string HeaderName = "Autocrypt";

    public sealed record Header(string Address, bool PreferEncrypt, PgpPublicKeyRing Key);

    /// <summary>The sender's Autocrypt header, if there is exactly one readable one for the From address.</summary>
    public static Header? Read(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.From.Mailboxes.FirstOrDefault()?.Address is not { } from)
        {
            return null;
        }

        var found = message.Headers.Where(h => h.Field.Equals(HeaderName, StringComparison.OrdinalIgnoreCase))
            .Select(h => Parse(h.Value))
            .Where(h => h is not null && string.Equals(h.Address, from, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>"addr=…; prefer-encrypt=mutual; keydata=…" → the header, or null if it is not valid.</summary>
    public static Header? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string? address = null;
        string? keydata = null;
        var preferEncrypt = false;
        foreach (var part in value.Split(';'))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var name = part[..equals].Trim().ToLowerInvariant();
            var text = part[(equals + 1)..].Trim();
            switch (name)
            {
                case "addr":
                    address = text;
                    break;
                case "prefer-encrypt":
                    preferEncrypt = text.Equals("mutual", StringComparison.OrdinalIgnoreCase);
                    break;
                case "keydata":
                    keydata = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
                    break;
                default:
                    // Unknown attributes starting with "_" may be ignored; any other one makes the header invalid.
                    if (!name.StartsWith('_'))
                    {
                        return null;
                    }

                    break;
            }
        }

        if (address is null || keydata is null)
        {
            return null;
        }

        try
        {
            var (rings, _) = PgpKeyParser.Read(Convert.FromBase64String(keydata));
            return rings.Count == 1 ? new Header(address, preferEncrypt, rings[0]) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The header value for an own key: address, mutual if wanted, and the key without line breaks (folded when written).</summary>
    public static string Format(string address, PgpPublicKeyRing key, bool preferEncrypt)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"addr={address}; {(preferEncrypt ? "prefer-encrypt=mutual; " : string.Empty)}keydata={Convert.ToBase64String(key.GetEncoded())}";
    }
}
