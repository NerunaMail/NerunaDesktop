using System.Security.Cryptography;
using System.Text;

namespace Neruna.Vault;

/// <summary>
/// Mail accounts the organisation set up in the portal, zero knowledge – the same scheme as certificates (see
/// <see cref="CertificateEnvelopes"/>), with its own contexts so a certificate key can never pass for an account key:
/// the account (servers, login, password, aliases) encrypted with a random key and signed by the organisation; that key
/// wrapped for each approved device of an assigned person.
/// </summary>
public static class AccountEnvelopes
{
    public static string Context(string accountId, string recipientId) => $"neruna-account-key-v1|{accountId}|{recipientId}";

    /// <exception cref="CryptographicException">Not for this device or this account, or changed.</exception>
    public static byte[] Unwrap(string envelope, ECDiffieHellman deviceKey, string accountId, string deviceId) =>
        CertificateEnvelopes.UnwrapFor(envelope, deviceKey, Context(accountId, deviceId));

    /// <summary>The account as the portal wrote it (JSON).</summary>
    /// <exception cref="CryptographicException">Wrong key, another account's payload, or changed.</exception>
    public static string Decrypt(string accountId, string payload, byte[] accountKey) =>
        Encoding.UTF8.GetString(CertificateEnvelopes.OpenPayload(payload, accountKey, "neruna-account-payload-v1|" + accountId));

    /// <summary>
    /// The organisation signed exactly this payload for exactly this account – servers included, so nobody but the
    /// organisation can point the password at another server.
    /// </summary>
    public static bool Verify(string accountId, string payload, string signature, string signingPublicKey) =>
        CertificateEnvelopes.VerifySigned($"neruna-account-v1|{accountId}|{payload}", signature, signingPublicKey);
}
