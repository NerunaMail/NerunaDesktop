using System.Security.Cryptography;
using System.Text;

namespace Neruna.Vault;

/// <summary>
/// What a desktop shows to pair a phone (QR code; the PIN separately). <see cref="Secret"/> never goes to the server:
/// the phone proves its encryption key with it, so the desktop re-encrypts certificates and accounts only for the
/// phone that scanned the code – not for a key the server might slip in. <see cref="OrganizationKeyHash"/> lets the
/// phone pin the organisation's signing key without trusting the server for it. See contract/openapi.yaml.
/// </summary>
public sealed record PairingInvitation(Uri Server, string Code, byte[] Secret, string? OrganizationKeyHash)
{
    private const string Scheme = "neruna-pair:1";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(32);

    /// <summary>base64url SHA-256 of the organisation's signing key (base64 SPKI).</summary>
    public static string HashOfKey(string signingPublicKey) => Base64Url(SHA256.HashData(Convert.FromBase64String(signingPublicKey)));

    public override string ToString() =>
        $"{Scheme}?server={Uri.EscapeDataString(Server.AbsoluteUri)}&code={Uri.EscapeDataString(Code)}&secret={Base64Url(Secret)}&org={OrganizationKeyHash ?? string.Empty}";

    /// <exception cref="FormatException">Not a Neruna pairing code.</exception>
    public static PairingInvitation Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (!text.StartsWith(Scheme + "?", StringComparison.Ordinal))
        {
            throw new FormatException("Not a Neruna pairing code.");
        }

        var values = text[(Scheme.Length + 1)..].Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        if (!values.TryGetValue("server", out var server) || !Uri.TryCreate(server, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("https" or "http")
            || !values.TryGetValue("code", out var code) || code.Length == 0
            || !values.TryGetValue("secret", out var secret))
        {
            throw new FormatException("Incomplete Neruna pairing code.");
        }

        var secretBytes = FromBase64Url(secret);
        if (secretBytes.Length != 32)
        {
            throw new FormatException("Invalid pairing secret.");
        }

        var org = values.GetValueOrDefault("org");
        return new PairingInvitation(serverUri, code, secretBytes, string.IsNullOrEmpty(org) ? null : org);
    }

    /// <summary>The phone's proof for its encryption key: base64 HMAC-SHA256(secret, "neruna-pair-v1|deviceId|key").</summary>
    public static string Proof(byte[] secret, string deviceId, string encryptionPublicKey) =>
        Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"neruna-pair-v1|{deviceId}|{encryptionPublicKey}")));

    public static bool VerifyProof(byte[] secret, string deviceId, string encryptionPublicKey, string? proof)
    {
        if (proof is null)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(Proof(secret, deviceId, encryptionPublicKey)), Convert.FromBase64String(proof));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        try
        {
            return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
        }
        catch (FormatException ex)
        {
            throw new FormatException("Invalid pairing secret.", ex);
        }
    }
}
