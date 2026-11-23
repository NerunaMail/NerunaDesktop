using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Neruna.Core.Security;

/// <summary>Where a PGP key on this device comes from – decides how far it is trusted.</summary>
public enum PgpKeySource
{
    /// <summary>Imported by the user from a file (own key with its secret part, or someone else's public key).</summary>
    Imported,

    /// <summary>Made in Neruna (Einstellungen → Zertifikate &amp; Schlüssel).</summary>
    Generated,

    /// <summary>Assigned by the organisation through Neruna Cloud/Control (removed when no longer assigned).</summary>
    Cloud,

    /// <summary>A colleague's public key from the organisation's directory (signed by the organisation key).</summary>
    Organization,

    /// <summary>Kept with a contact in the address book (vCard KEY), not here.</summary>
    Contact,

    /// <summary>Sent along in a mail (Autocrypt header): known, not confirmed.</summary>
    Autocrypt,

    /// <summary>Found on the recipient's mail server (WKD) or on keys.openpgp.org: known, not confirmed.</summary>
    KeyServer,
}

/// <summary>How a PGP key is kept: the public key ring, and for own keys the secret one (protected by its passphrase).</summary>
/// <param name="Verified">The user confirmed the fingerprint (or it came from a source that counts as confirmed).</param>
public sealed record StoredPgpKey(string Fingerprint, byte[] PublicKeyRing, byte[]? SecretKeyRing, PgpKeySource Source, bool Verified, DateTimeOffset AddedAt);

public interface IPgpKeyStore
{
    Task<IReadOnlyList<StoredPgpKey>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates; a secret key ring is never dropped by a later public-only import.</summary>
    Task SaveAsync(StoredPgpKey key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fingerprint, CancellationToken cancellationToken = default);
}

/// <summary>
/// Other people's public PGP keys kept with their contacts (vCard KEY, CardDAV) instead of only on this device: they
/// reach every device and program that uses the address book.
/// </summary>
public interface IContactPgpKeys
{
    /// <summary>The keys (binary key rings) of all contacts, with the addresses of the contact each belongs to.</summary>
    Task<IReadOnlyList<(byte[] Key, IReadOnlyList<string> Emails)>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds the key to every writable contact with one of <paramref name="emails"/>.</summary>
    /// <returns>Whether a contact has it now; false: no such contact, or it could not be saved.</returns>
    Task<bool> AddAsync(byte[] key, IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default);

    /// <summary>Takes the key out of every contact that has it.</summary>
    Task RemoveAsync(byte[] key, CancellationToken cancellationToken = default);
}

/// <summary>What the key overview shows about a PGP key.</summary>
public sealed record PgpKeyInfo(
    string Fingerprint,
    string KeyId,
    IReadOnlyList<string> UserIds,
    IReadOnlyList<string> EmailAddresses,
    string Algorithm,
    DateTimeOffset Created,
    DateTimeOffset? Expires,
    bool IsRevoked,
    bool CanSign,
    bool CanEncrypt,
    bool HasSecretKey,
    PgpKeySource Source,
    bool Verified,
    DateTimeOffset AddedAt)
{
    /// <summary>"Anna Muster" from "Anna Muster &lt;anna@example.com&gt;"; else the address or the key ID.</summary>
    public string Name => UserIds.Select(PgpKeyParser.NameOf).FirstOrDefault(n => n.Length > 0) ?? EmailAddresses.FirstOrDefault() ?? KeyId;

    /// <summary>The fingerprint in groups of four, as programs show it to compare.</summary>
    public string FingerprintText => PgpKeyParser.Grouped(Fingerprint);

    public CertificateStatus StatusAt(DateTimeOffset now) =>
        now < Created ? CertificateStatus.NotYetValid
        : IsRevoked || (Expires is { } end && now > end) ? CertificateStatus.Expired
        : Expires is { } soon && soon - now < CertificateInfo.ExpiryWarning ? CertificateStatus.ExpiringSoon
        : CertificateStatus.Valid;

    public bool IsUsableAt(DateTimeOffset now) => StatusAt(now) is CertificateStatus.Valid or CertificateStatus.ExpiringSoon;

    public bool Covers(string emailAddress) => EmailAddresses.Contains(emailAddress, StringComparer.OrdinalIgnoreCase);

    /// <summary>Trusted for a "valid" signature: own, the organisation's, the contact's, or confirmed by the user.</summary>
    public bool IsTrusted => Verified || HasSecretKey || Source is PgpKeySource.Cloud or PgpKeySource.Organization or PgpKeySource.Contact or PgpKeySource.Generated;
}

/// <summary>Reads, describes and writes OpenPGP keys (armored or binary).</summary>
public static class PgpKeyParser
{
    /// <summary>The key rings in <paramref name="data"/>: public ones, and secret ones with their public part.</summary>
    /// <exception cref="FormatException">No OpenPGP key in it.</exception>
    public static (IReadOnlyList<PgpPublicKeyRing> Public, IReadOnlyList<PgpSecretKeyRing> Secret) Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var publicRings = new List<PgpPublicKeyRing>();
        var secretRings = new List<PgpSecretKeyRing>();
        try
        {
            using var input = PgpUtilities.GetDecoderStream(new MemoryStream(data));
            var factory = new PgpObjectFactory(input);
            while (factory.NextPgpObject() is { } item)
            {
                switch (item)
                {
                    case PgpPublicKeyRing ring:
                        publicRings.Add(ring);
                        break;
                    case PgpSecretKeyRing ring:
                        secretRings.Add(ring);
                        break;
                }
            }

            // Several armored blocks one after the other (a public and a private block in one .asc file).
            if (IsArmored(data))
            {
                var text = Encoding.ASCII.GetString(data);
                var second = text.IndexOf("-----BEGIN PGP", text.IndexOf("-----END PGP", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
                if (second > 0)
                {
                    var (morePublic, moreSecret) = Read(Encoding.ASCII.GetBytes(text[second..]));
                    publicRings.AddRange(morePublic);
                    secretRings.AddRange(moreSecret);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or PgpException or ArgumentException or InvalidOperationException)
        {
            if (publicRings.Count == 0 && secretRings.Count == 0)
            {
                throw new FormatException("Not an OpenPGP key.", ex);
            }
        }

        if (publicRings.Count == 0 && secretRings.Count == 0)
        {
            throw new FormatException("Not an OpenPGP key.");
        }

        return (publicRings, secretRings);
    }

    private static bool IsArmored(byte[] data) => data.Length > 10 && Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 200)).Contains("-----BEGIN PGP", StringComparison.Ordinal);

    /// <summary>The public part of a secret key ring.</summary>
    public static PgpPublicKeyRing PublicOf(PgpSecretKeyRing secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        using var stream = new MemoryStream();
        foreach (var key in secret.GetSecretKeys().Select(k => k.PublicKey))
        {
            var packet = key.GetEncoded();
            if (!key.IsMasterKey)
            {
                // The public part of a secret subkey is written as a primary key packet (tag 6); in a public key ring
                // it must be a subkey packet (tag 14), or readers see a second key ring.
                packet[0] = (packet[0] & 0x40) != 0 ? (byte)0xCE : (byte)(0x80 | (14 << 2) | (packet[0] & 0x03));
            }

            stream.Write(packet);
        }

        return new PgpPublicKeyRing(stream.ToArray());
    }

    public static string FingerprintOf(PgpPublicKeyRing ring) => FingerprintOf(ring.GetPublicKey());

    public static string FingerprintOf(PgpPublicKey key) => Convert.ToHexString(key.GetFingerprint());

    public static string Grouped(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, (fingerprint.Length + 3) / 4).Select(i => fingerprint.Substring(i * 4, Math.Min(4, fingerprint.Length - i * 4))));

    /// <summary>The address in a user ID ("Anna Muster &lt;anna@example.com&gt;" or a bare address); null without one.</summary>
    public static string? AddressOf(string userId)
    {
        var start = userId.LastIndexOf('<');
        var end = userId.LastIndexOf('>');
        var address = start >= 0 && end > start ? userId[(start + 1)..end] : userId;
        address = address.Trim();
        return address.Contains('@', StringComparison.Ordinal) && !address.Contains(' ', StringComparison.Ordinal) ? address : null;
    }

    public static string NameOf(string userId)
    {
        var start = userId.LastIndexOf('<');
        return (start > 0 ? userId[..start] : AddressOf(userId) is null ? userId : string.Empty).Trim().Trim('"');
    }

    /// <summary>Describes the primary key of a ring and what its keys can do now.</summary>
    public static PgpKeyInfo Describe(PgpPublicKeyRing ring, bool hasSecretKey, PgpKeySource source, bool verified, DateTimeOffset addedAt)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var primary = ring.GetPublicKey();
        var userIds = primary.GetUserIds().OfType<string>().Where(u => u.Length > 0).ToList();
        var emails = userIds.Select(AddressOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var created = new DateTimeOffset(DateTime.SpecifyKind(primary.CreationTime, DateTimeKind.Utc));
        var seconds = primary.GetValidSeconds();
        DateTimeOffset? expires = seconds > 0 ? created.AddSeconds(seconds) : null;
        var keys = ring.GetPublicKeys().ToList();
        return new PgpKeyInfo(
            FingerprintOf(primary),
            primary.KeyId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            userIds,
            emails,
            AlgorithmName(primary),
            created,
            expires,
            primary.HasRevocation(),
            keys.Any(k => CanSign(k, primary)),
            keys.Any(k => k.IsEncryptionKey && !k.HasRevocation() && Flags(k, primary) is var f && (f == 0 || (f & (PgpKeyFlags.CanEncryptCommunications | PgpKeyFlags.CanEncryptStorage)) != 0)),
            hasSecretKey,
            source,
            verified,
            addedAt);
    }

    /// <summary>A key that may sign: the primary key unless its flags say otherwise, or a subkey flagged for signing.</summary>
    public static bool CanSign(PgpPublicKey key, PgpPublicKey primary)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.HasRevocation() || key.Algorithm is PublicKeyAlgorithmTag.ElGamalEncrypt or PublicKeyAlgorithmTag.ECDH or PublicKeyAlgorithmTag.RsaEncrypt)
        {
            return false;
        }

        var flags = Flags(key, primary);
        return flags == 0 ? key.IsMasterKey : (flags & PgpKeyFlags.CanSign) != 0;
    }

    // The key flags from the binding signature (subkeys) or the self-signature (primary key); 0: none given.
    private static int Flags(PgpPublicKey key, PgpPublicKey primary)
    {
        IEnumerable<PgpSignature> signatures = key.IsMasterKey
            ? key.GetSignatures().Where(s => s.KeyId == key.KeyId)
            : key.GetSignaturesOfType(PgpSignature.SubkeyBinding).Where(s => s.KeyId == primary.KeyId);
        foreach (var signature in signatures.OrderByDescending(s => s.CreationTime))
        {
            if (signature.GetHashedSubPackets() is { } packets && packets.HasSubpacket(SignatureSubpacketTag.KeyFlags))
            {
                return packets.GetKeyFlags();
            }
        }

        return 0;
    }

    private static string AlgorithmName(PgpPublicKey key) => key.Algorithm switch
    {
        PublicKeyAlgorithmTag.EdDsa_Legacy or PublicKeyAlgorithmTag.Ed25519 => "Ed25519",
        PublicKeyAlgorithmTag.Ed448 => "Ed448",
        PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign or PublicKeyAlgorithmTag.RsaEncrypt => $"RSA {key.BitStrength}",
        PublicKeyAlgorithmTag.ECDsa => $"ECDSA {key.BitStrength}",
        PublicKeyAlgorithmTag.Dsa => $"DSA {key.BitStrength}",
        _ => key.Algorithm.ToString(),
    };

    /// <summary>The public key ring as an .asc text block.</summary>
    public static string Armor(PgpPublicKeyRing ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        using var stream = new MemoryStream();
        using (var armored = new ArmoredOutputStream(stream))
        {
            ring.Encode(armored);
        }

        return Encoding.ASCII.GetString(stream.ToArray());
    }

    /// <summary>The secret key ring as an .asc text block (still protected by its passphrase).</summary>
    public static string Armor(PgpSecretKeyRing ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        using var stream = new MemoryStream();
        using (var armored = new ArmoredOutputStream(stream))
        {
            ring.Encode(armored);
        }

        return Encoding.ASCII.GetString(stream.ToArray());
    }

    /// <summary>Whether <paramref name="passphrase"/> opens the secret keys of the ring.</summary>
    public static bool Opens(PgpSecretKeyRing ring, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(ring);
        try
        {
            foreach (var key in ring.GetSecretKeys().Where(k => !k.IsPrivateKeyEmpty))
            {
                _ = key.ExtractPrivateKey(passphrase.ToCharArray());
            }

            return true;
        }
        catch (PgpException)
        {
            return false;
        }
    }

    /// <summary>The same secret keys under another passphrase (e.g. for a backup file the user protects).</summary>
    public static PgpSecretKeyRing WithPassphrase(PgpSecretKeyRing ring, string oldPassphrase, string newPassphrase) =>
        PgpSecretKeyRing.CopyWithNewPassword(ring, oldPassphrase.ToCharArray(), newPassphrase.ToCharArray(), SymmetricKeyAlgorithmTag.Aes256, new SecureRandom());
}

/// <summary>New keys as Thunderbird makes them: Ed25519 to sign, Curve25519 to encrypt, valid for three years.</summary>
public static class PgpKeyGenerator
{
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(3 * 365);

    /// <summary>A key ring for <paramref name="name"/> &lt;<paramref name="email"/>&gt;, protected by <paramref name="passphrase"/>.</summary>
    public static PgpSecretKeyRing Generate(string? name, string email, string passphrase, DateTime now, TimeSpan? validity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(passphrase);
        var random = new SecureRandom();
        var signing = new Ed25519KeyPairGenerator();
        signing.Init(new Ed25519KeyGenerationParameters(random));
        var encryption = new X25519KeyPairGenerator();
        encryption.Init(new X25519KeyGenerationParameters(random));
        var created = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var primary = new PgpKeyPair(PublicKeyAlgorithmTag.EdDsa_Legacy, signing.GenerateKeyPair(), created);
        var subkey = new PgpKeyPair(PublicKeyAlgorithmTag.ECDH, encryption.GenerateKeyPair(), created);
        var seconds = (long)(validity ?? DefaultValidity).TotalSeconds;

        var primaryPackets = new PgpSignatureSubpacketGenerator();
        primaryPackets.SetKeyFlags(false, PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
        primaryPackets.SetKeyExpirationTime(false, seconds);
        primaryPackets.SetPreferredHashAlgorithms(false, [(int)HashAlgorithmTag.Sha512, (int)HashAlgorithmTag.Sha384, (int)HashAlgorithmTag.Sha256]);
        primaryPackets.SetPreferredSymmetricAlgorithms(false, [(int)SymmetricKeyAlgorithmTag.Aes256, (int)SymmetricKeyAlgorithmTag.Aes128]);
        primaryPackets.SetPreferredCompressionAlgorithms(false, [(int)CompressionAlgorithmTag.Zip, (int)CompressionAlgorithmTag.Uncompressed]);
        primaryPackets.SetFeature(false, Org.BouncyCastle.Bcpg.Sig.Features.FEATURE_MODIFICATION_DETECTION);
        var subkeyPackets = new PgpSignatureSubpacketGenerator();
        subkeyPackets.SetKeyFlags(false, PgpKeyFlags.CanEncryptCommunications | PgpKeyFlags.CanEncryptStorage);
        subkeyPackets.SetKeyExpirationTime(false, seconds);

        var userId = string.IsNullOrWhiteSpace(name) ? $"<{email.Trim()}>" : $"{name.Trim()} <{email.Trim()}>";
        var generator = new PgpKeyRingGenerator(PgpSignature.PositiveCertification, primary, userId, SymmetricKeyAlgorithmTag.Aes256,
            passphrase.ToCharArray(), true, primaryPackets.Generate(), null, random);
        generator.AddSubKey(subkey, subkeyPackets.Generate(), null);
        return generator.GenerateSecretKeyRing();
    }

    /// <summary>A long random passphrase for a key made here (kept in the system's key store, as Thunderbird does).</summary>
    public static string RandomPassphrase() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}
