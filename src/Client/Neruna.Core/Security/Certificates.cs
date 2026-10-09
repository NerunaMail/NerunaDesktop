using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Security;

public enum CertificateSource
{
    Imported,

    /// <summary>Taken automatically from a validly signed message (common practice for S/MIME clients).</summary>
    CollectedFromMail,

    /// <summary>Provided by the organisation through Neruna Cloud/Control (removed here when it is no longer assigned).</summary>
    Cloud,
}

public enum CertificateStatus
{
    Valid,
    ExpiringSoon,
    Expired,
    NotYetValid,
}

/// <summary>What the certificate overview shows. Own certificates have a private key (sign/decrypt).</summary>
public sealed record CertificateInfo(
    string Thumbprint,
    string SubjectName,
    IReadOnlyList<string> EmailAddresses,
    string IssuerName,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool HasPrivateKey,
    bool IsAuthority,
    bool CanSign,
    bool CanEncrypt,
    CertificateSource Source,
    DateTimeOffset AddedAt)
{
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    public CertificateStatus StatusAt(DateTimeOffset now) =>
        now < NotBefore ? CertificateStatus.NotYetValid
        : now > NotAfter ? CertificateStatus.Expired
        : NotAfter - now < ExpiryWarning ? CertificateStatus.ExpiringSoon
        : CertificateStatus.Valid;

    public bool IsUsableAt(DateTimeOffset now) => StatusAt(now) is CertificateStatus.Valid or CertificateStatus.ExpiringSoon;

    public bool Covers(string emailAddress) => EmailAddresses.Contains(emailAddress, StringComparer.OrdinalIgnoreCase);
}

/// <summary>How a certificate is persisted. The PKCS#12 container stays encrypted with its own password.</summary>
public sealed record StoredCertificate(string Thumbprint, byte[] Der, byte[]? Pkcs12, CertificateSource Source, DateTimeOffset AddedAt);

public interface ICertificateStore
{
    Task<IReadOnlyList<StoredCertificate>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates; an existing private key is never dropped by a later public-only import.</summary>
    Task SaveAsync(StoredCertificate certificate, CancellationToken cancellationToken = default);

    Task DeleteAsync(string thumbprint, CancellationToken cancellationToken = default);
}

public sealed class CertificatePasswordException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An own certificate with its private key and chain, ready for signing and decryption.</summary>
public sealed record PrivateKeyEntry(X509Certificate Certificate, AsymmetricKeyParameter PrivateKey, IReadOnlyList<X509Certificate> Chain);

/// <summary>Everything S/MIME needs, loaded once per operation.</summary>
public sealed record SecureMimeMaterial(
    IReadOnlyList<PrivateKeyEntry> PrivateKeys,
    IReadOnlyList<X509Certificate> Certificates,
    IReadOnlyList<X509Certificate> Authorities,
    IReadOnlyList<CertificateInfo> Infos);

/// <summary>Parsing helpers on top of BouncyCastle (which MimeKit uses for S/MIME anyway).</summary>
public static class CertificateParser
{
    private static readonly DerObjectIdentifier EmailProtection = new("1.3.6.1.5.5.7.3.4");
    private static readonly DerObjectIdentifier AnyExtendedKeyUsage = new("2.5.29.37.0");

    /// <summary>Reads DER, PEM (one or more certificates) or PKCS#7 (.p7b). Returns an empty list if the data is none of these.</summary>
    public static IReadOnlyList<X509Certificate> ReadCertificates(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (LooksLikePem(data))
        {
            // Only certificate blocks are read; private keys or other blocks in the same file are skipped.
            var certificates = new List<X509Certificate>();
            using var reader = new PemReader(new StringReader(Encoding.ASCII.GetString(data)));
            while (reader.ReadPemObject() is { } block)
            {
                if (block.Type is "CERTIFICATE" or "X509 CERTIFICATE" or "TRUSTED CERTIFICATE")
                {
                    certificates.Add(new X509CertificateParser().ReadCertificate(block.Content));
                }
                else if (block.Type is "PKCS7" or "CMS")
                {
                    certificates.AddRange(new CmsSignedData(block.Content).GetCertificates().EnumerateMatches(null));
                }
            }

            return certificates;
        }

        try
        {
            // BouncyCastle returns null (instead of throwing) for some non-certificate input.
            if (new X509CertificateParser().ReadCertificate(data) is { } certificate)
            {
                return [certificate];
            }
        }
        catch (Exception ex) when (ex is Org.BouncyCastle.Security.Certificates.CertificateException or IOException or ArgumentException or InvalidCastException)
        {
        }

        try
        {
            return new CmsSignedData(data).GetCertificates().EnumerateMatches(null).ToList();
        }
        catch (Exception ex) when (ex is CmsException or IOException or ArgumentException or InvalidCastException)
        {
            return [];
        }
    }

    /// <exception cref="CertificatePasswordException">Wrong password or not a PKCS#12 file.</exception>
    public static IReadOnlyList<PrivateKeyEntry> ReadPkcs12(byte[] data, string password)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(password);

        var store = new Pkcs12StoreBuilder().Build();
        try
        {
            store.Load(new MemoryStream(data), password.ToCharArray());
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidCastException or Org.BouncyCastle.Security.GeneralSecurityException)
        {
            throw new CertificatePasswordException(T("Das Passwort ist falsch oder die Datei ist kein gültiges PKCS#12-Zertifikat (.p12/.pfx)."), ex);
        }

        var entries = new List<PrivateKeyEntry>();
        foreach (var alias in store.Aliases)
        {
            if (!store.IsKeyEntry(alias))
            {
                continue;
            }

            var chain = store.GetCertificateChain(alias)?.Select(c => c.Certificate).ToList() ?? [];
            var certificate = store.GetCertificate(alias)?.Certificate ?? chain.FirstOrDefault();
            if (certificate is not null)
            {
                entries.Add(new PrivateKeyEntry(certificate, store.GetKey(alias).Key, chain.Count > 0 ? chain : [certificate]));
            }
        }

        return entries;
    }

    public static string Thumbprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
#pragma warning disable CA5350 // SHA-1 is the conventional certificate thumbprint shown by Windows; not used for security.
        return Convert.ToHexString(SHA1.HashData(certificate.GetEncoded()));
#pragma warning restore CA5350
    }

    public static CertificateInfo Describe(X509Certificate certificate, bool hasPrivateKey, CertificateSource source, DateTimeOffset addedAt)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var keyUsage = certificate.GetKeyUsage();
        var extendedUsage = certificate.GetExtendedKeyUsage();
        var emailAllowed = extendedUsage is null || extendedUsage.Count == 0
                           || extendedUsage.Contains(EmailProtection) || extendedUsage.Contains(AnyExtendedKeyUsage);
        var isAuthority = certificate.GetBasicConstraints() >= 0;

        return new CertificateInfo(
            Thumbprint(certificate),
            CommonName(certificate.SubjectDN) ?? certificate.SubjectDN.ToString(),
            EmailAddresses(certificate),
            CommonName(certificate.IssuerDN) ?? certificate.IssuerDN.ToString(),
            new DateTimeOffset(DateTime.SpecifyKind(certificate.NotBefore, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(certificate.NotAfter, DateTimeKind.Utc)),
            hasPrivateKey,
            isAuthority,
            emailAllowed && !isAuthority && (keyUsage is null || keyUsage[0] || keyUsage[1]),
            emailAllowed && !isAuthority && (keyUsage is null || keyUsage[2] || keyUsage[4]),
            source,
            addedAt);
    }

    public static IReadOnlyList<string> EmailAddresses(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var addresses = new List<string>();

        foreach (var name in certificate.GetSubjectAlternativeNames() ?? [])
        {
            if (name.Count >= 2 && name[0] is int tag && tag == GeneralName.Rfc822Name && name[1] is string email)
            {
                addresses.Add(email.Trim());
            }
        }

        addresses.AddRange(certificate.SubjectDN.GetValueList(X509Name.EmailAddress).Select(e => e.Trim()));
        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? CommonName(X509Name name) => name.GetValueList(X509Name.CN).FirstOrDefault()
                                                        ?? name.GetValueList(X509Name.O).FirstOrDefault();

    private static bool LooksLikePem(byte[] data) =>
        Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 4096)).Contains("-----BEGIN ", StringComparison.Ordinal);
}

/// <summary>
/// The user's S/MIME certificates: import, list, export, remove. Passwords of PKCS#12 containers are kept
/// in the <see cref="ICredentialStore"/>, never in the database.
/// </summary>
public sealed class CertificateManager(ICertificateStore store, ICredentialStore secrets, TimeProvider clock)
{
    public async Task<IReadOnlyList<CertificateInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var parser = new X509CertificateParser();
        return (await store.GetAllAsync(cancellationToken))
            .Select(s => CertificateParser.Describe(parser.ReadCertificate(s.Der), s.Pkcs12 is not null, s.Source, s.AddedAt))
            .OrderByDescending(c => c.HasPrivateKey)
            .ThenBy(c => c.EmailAddresses.FirstOrDefault() ?? c.SubjectName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(c => c.NotAfter)
            .ToList();
    }

    /// <summary>Imports certificates (.cer/.crt/.pem/.der/.p7b) or a PKCS#12 container (.p12/.pfx) with private key.</summary>
    /// <exception cref="CertificatePasswordException">PKCS#12 without or with a wrong password.</exception>
    /// <exception cref="FormatException">Not a certificate file.</exception>
    public async Task<IReadOnlyList<CertificateInfo>> ImportAsync(byte[] data, string? password, CertificateSource source = CertificateSource.Imported, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var now = clock.GetUtcNow();

        var certificates = CertificateParser.ReadCertificates(data);
        if (certificates.Count > 0)
        {
            var imported = new List<CertificateInfo>();
            foreach (var certificate in certificates)
            {
                var thumbprint = CertificateParser.Thumbprint(certificate);
                await store.SaveAsync(new StoredCertificate(thumbprint, certificate.GetEncoded(), null, source, now), cancellationToken);
                imported.Add(CertificateParser.Describe(certificate, false, source, now));
            }

            return imported;
        }

        var entries = CertificateParser.ReadPkcs12(data, password ?? string.Empty);
        if (entries.Count == 0)
        {
            throw new FormatException(T("Die Datei enthält kein Zertifikat mit privatem Schlüssel."));
        }

        var result = new List<CertificateInfo>();
        foreach (var entry in entries)
        {
            var thumbprint = CertificateParser.Thumbprint(entry.Certificate);
            await secrets.SetSecretAsync(SecretId(thumbprint), password ?? string.Empty, cancellationToken);
            await store.SaveAsync(new StoredCertificate(thumbprint, entry.Certificate.GetEncoded(), data, source, now), cancellationToken);
            result.Add(CertificateParser.Describe(entry.Certificate, true, source, now));
        }

        return result;
    }

    /// <summary>Keeps the signer certificate of a valid signature so the user can encrypt to that person later.</summary>
    public async Task<bool> CollectAsync(X509Certificate certificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var thumbprint = CertificateParser.Thumbprint(certificate);
        if ((await store.GetAllAsync(cancellationToken)).Any(c => c.Thumbprint == thumbprint))
        {
            return false;
        }

        await store.SaveAsync(new StoredCertificate(thumbprint, certificate.GetEncoded(), null, CertificateSource.CollectedFromMail, clock.GetUtcNow()), cancellationToken);
        return true;
    }

    /// <summary>The public certificate (DER), e.g. to send it to a contact.</summary>
    public async Task<byte[]> ExportPublicAsync(string thumbprint, CancellationToken cancellationToken = default) =>
        (await store.GetAllAsync(cancellationToken)).FirstOrDefault(c => c.Thumbprint == thumbprint)?.Der
        ?? throw new InvalidOperationException(T("Zertifikat nicht gefunden."));

    public async Task RemoveAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        await store.DeleteAsync(thumbprint, cancellationToken);
        await secrets.DeleteSecretAsync(SecretId(thumbprint), cancellationToken);
    }

    public async Task<SecureMimeMaterial> LoadMaterialAsync(CancellationToken cancellationToken = default)
    {
        var parser = new X509CertificateParser();
        var stored = await store.GetAllAsync(cancellationToken);
        var keys = new List<PrivateKeyEntry>();
        var certificates = new List<X509Certificate>();
        var authorities = new List<X509Certificate>();
        var infos = new List<CertificateInfo>();

        foreach (var item in stored)
        {
            var certificate = parser.ReadCertificate(item.Der);
            var info = CertificateParser.Describe(certificate, item.Pkcs12 is not null, item.Source, item.AddedAt);
            infos.Add(info);
            certificates.Add(certificate);
            if (info.IsAuthority)
            {
                authorities.Add(certificate);
            }

            if (item.Pkcs12 is not null && await secrets.GetSecretAsync(SecretId(item.Thumbprint), cancellationToken) is { } password)
            {
                try
                {
                    keys.AddRange(CertificateParser.ReadPkcs12(item.Pkcs12, password).Where(k => CertificateParser.Thumbprint(k.Certificate) == item.Thumbprint));
                }
                catch (CertificatePasswordException)
                {
                    // Stored password no longer matches; the certificate stays usable for verification only.
                }
            }
        }

        return new SecureMimeMaterial(keys, certificates, authorities, infos);
    }

    /// <summary>
    /// The user's own certificates for a settings backup: imported and collected ones with the PKCS#12 password –
    /// never those from the organisation (they come back through the cloud).
    /// </summary>
    public async Task<IReadOnlyList<(StoredCertificate Certificate, string? Password)>> GetPersonalAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<(StoredCertificate, string?)>();
        foreach (var item in (await store.GetAllAsync(cancellationToken)).Where(c => c.Source != CertificateSource.Cloud).OrderBy(c => c.Thumbprint, StringComparer.Ordinal))
        {
            result.Add((item, item.Pkcs12 is null ? null : await secrets.GetSecretAsync(SecretId(item.Thumbprint), cancellationToken)));
        }

        return result;
    }

    /// <summary>Restoring a backup: the own certificates become exactly <paramref name="certificates"/>; cloud ones stay.</summary>
    public async Task ReplacePersonalAsync(IReadOnlyList<(StoredCertificate Certificate, string? Password)> certificates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        var keep = certificates.Select(c => c.Certificate.Thumbprint).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in (await store.GetAllAsync(cancellationToken)).Where(c => c.Source != CertificateSource.Cloud && !keep.Contains(c.Thumbprint)))
        {
            await RemoveAsync(stale.Thumbprint, cancellationToken);
        }

        foreach (var (certificate, password) in certificates)
        {
            await store.SaveAsync(certificate, cancellationToken);
            if (certificate.Pkcs12 is not null && password is not null)
            {
                await secrets.SetSecretAsync(SecretId(certificate.Thumbprint), password, cancellationToken);
            }
        }
    }

    // A stable credential-store id per certificate.
    private static Guid SecretId(string thumbprint) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("neruna-cert:" + thumbprint)).AsSpan(0, 16));
}
