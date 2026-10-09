using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neruna.Vault;

/// <summary>
/// The device side of S/MIME certificates from Neruna Cloud/Control (format: server/public/js/neruna-certificates.js).
/// The portal encrypts a PKCS#12 with its password under a random certificate key and signs it with the organisation
/// key; the certificate key is wrapped for each approved device (ECDH P-256 → HKDF-SHA256 → AES-256-GCM). Only this
/// device's private key opens its envelope; the server holds nothing it could decrypt.
/// </summary>
public static class CertificateEnvelopes
{
    private static readonly byte[] EnvelopeSalt = Encoding.UTF8.GetBytes("neruna-envelope-v1");

    /// <summary>What an envelope is bound to: this certificate and this recipient (a device id).</summary>
    public static string Context(string certificateId, string recipientId) => $"neruna-cert-key-v1|{certificateId}|{recipientId}";

    /// <summary>A new encryption key pair for this device (its private half belongs into the system keychain).</summary>
    public static ECDiffieHellman CreateDeviceKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string PublicKeyOf(ECDiffieHellman key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static ECDiffieHellman ImportDeviceKey(byte[] pkcs8)
    {
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return key;
    }

    /// <summary>The certificate key from this device's envelope.</summary>
    /// <exception cref="CryptographicException">Not for this device or this certificate, or changed.</exception>
    public static byte[] Unwrap(string envelope, ECDiffieHellman deviceKey, string certificateId, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceKey);
        var parsed = JsonSerializer.Deserialize<Sealed>(envelope) ?? throw new CryptographicException("Empty envelope.");
        if (parsed.v != 1 || parsed.epk is null)
        {
            throw new CryptographicException("Unknown envelope format.");
        }

        var context = Context(certificateId, deviceId);
        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(Convert.FromBase64String(parsed.epk), out _);
        var shared = deviceKey.DeriveRawSecretAgreement(ephemeral.PublicKey);
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, EnvelopeSalt, Encoding.UTF8.GetBytes(context));
        return Open(kek, parsed, context);
    }

    /// <summary>The PKCS#12 and its password.</summary>
    public static (byte[] Pkcs12, string Password) Decrypt(string certificateId, string payload, byte[] certificateKey)
    {
        var parsed = JsonSerializer.Deserialize<Sealed>(payload) ?? throw new CryptographicException("Empty payload.");
        var plain = Open(certificateKey, parsed, "neruna-cert-payload-v1|" + certificateId);
        var content = JsonSerializer.Deserialize<Content>(plain) ?? throw new CryptographicException("Empty payload.");
        return (Convert.FromBase64String(content.pkcs12), content.password);
    }

    /// <summary>
    /// The organisation signed exactly this payload for exactly this certificate. Without that, a certificate is never
    /// taken – a server could otherwise slip in a key of its own (others would then encrypt to it).
    /// </summary>
    public static bool Verify(string certificateId, string payload, string signature, string signingPublicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signingPublicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes($"neruna-cert-v1|{certificateId}|{payload}"), Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Open(byte[] key, Sealed sealedData, string aad)
    {
        var iv = Convert.FromBase64String(sealedData.iv);
        var data = Convert.FromBase64String(sealedData.ct);
        if (iv.Length != 12 || data.Length < 16)
        {
            throw new CryptographicException("Invalid ciphertext.");
        }

        var plain = new byte[data.Length - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(iv, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(aad));
        return plain;
    }

#pragma warning disable SA1300, IDE1006 // The JSON of the browser module, field names as they are written there.
    private sealed record Sealed(int v, string? epk, string iv, string ct);

    private sealed record Content(string pkcs12, string password);
#pragma warning restore SA1300, IDE1006
}
