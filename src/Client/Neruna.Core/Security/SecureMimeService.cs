using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.Utilities.Collections;
using BcCertificate = Org.BouncyCastle.X509.X509Certificate;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Security;

public enum SignatureStatus
{
    /// <summary>Signature intact and the certificate chains to a trusted root.</summary>
    Valid,

    /// <summary>Signature intact, but the issuer is not trusted (e.g. a company CA that was not imported).</summary>
    ValidUntrusted,

    /// <summary>The content was changed after signing, or the signature cannot be checked at all.</summary>
    Invalid,
}

public sealed record SignatureInfo(
    string SignerName,
    IReadOnlyList<string> SignerEmails,
    string? IssuerName,
    SignatureStatus Status,
    bool EmailMatchesSender,
    DateTimeOffset? SignedAt,
    string? Detail);

/// <param name="DecryptionError">Set if the message was encrypted and could not be decrypted (e.g. no matching private key).</param>
public sealed record MessageSecurityInfo(bool WasEncrypted, string? DecryptionError, IReadOnlyList<SignatureInfo> Signatures)
{
    public static MessageSecurityInfo None { get; } = new(false, null, []);

    public bool IsSigned => Signatures.Count > 0;
}

/// <summary>A message with S/MIME layers removed, plus what was found on the way.</summary>
public sealed record OpenedMessage(MimeMessage Readable, MessageSecurityInfo Security);

/// <summary>What the compose window may offer for a given sender and recipients.</summary>
public sealed record SecureMimeCapabilities(bool CanSign, IReadOnlyList<string> RecipientsWithoutCertificate);

public sealed class SecureMimeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// S/MIME for Neruna: sign/encrypt outgoing mail, decrypt/verify incoming mail.
/// Uses MimeKit with a context fed from the user's <see cref="CertificateManager"/>.
/// </summary>
/// <summary>
/// The algorithms Neruna offers for S/MIME (Einstellungen → Zertifikate). Only current, widely supported ones: SHA-2 for
/// signatures, AES for encryption – the defaults are what most mail programs use today.
/// </summary>
public static class SecureMimeAlgorithms
{
    public static IReadOnlyList<(string Key, string Label)> Digests { get; } =
        [("sha256", "SHA-256 (Standard)"), ("sha384", "SHA-384"), ("sha512", "SHA-512")];

    public static IReadOnlyList<(string Key, string Label)> Ciphers { get; } =
        [("aes256", "AES-256 (Standard)"), ("aes192", "AES-192"), ("aes128", "AES-128 (für ältere Programme)")];

    public static DigestAlgorithm Digest(string? key) => key switch
    {
        "sha384" => DigestAlgorithm.Sha384,
        "sha512" => DigestAlgorithm.Sha512,
        _ => DigestAlgorithm.Sha256,
    };

    public static EncryptionAlgorithm Cipher(string? key) => key switch
    {
        "aes192" => EncryptionAlgorithm.Aes192,
        "aes128" => EncryptionAlgorithm.Aes128,
        _ => EncryptionAlgorithm.Aes256,
    };
}

public sealed class SecureMimeService(CertificateManager certificates, ISettingsStore settings, TimeProvider clock, ILogger<SecureMimeService> logger)
{
    public async Task<SecureMimeCapabilities> GetCapabilitiesAsync(string fromAddress, IEnumerable<string> recipients, CancellationToken cancellationToken = default)
    {
        var infos = await certificates.GetAllAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var canSign = infos.Any(c => c.HasPrivateKey && c.CanSign && c.IsUsableAt(now) && c.Covers(fromAddress));
        var missing = recipients
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !infos.Any(c => c.CanEncrypt && c.IsUsableAt(now) && c.Covers(r)))
            .ToList();
        return new SecureMimeCapabilities(canSign, missing);
    }

    /// <summary>Signs (clear-signed, readable everywhere) and/or encrypts the message in place.</summary>
    /// <exception cref="SecureMimeException">No own certificate, or certificates of some recipients are missing.</exception>
    public async Task ProtectAsync(MimeMessage message, bool sign, bool encrypt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!sign && !encrypt)
        {
            return;
        }

        var from = message.From.Mailboxes.FirstOrDefault() ?? throw new SecureMimeException(T("Die Nachricht hat keinen Absender."));
        using var context = await CreateContextAsync(cancellationToken);
        var body = message.Body ?? throw new SecureMimeException(T("Die Nachricht hat keinen Inhalt."));

        if (sign)
        {
            var signer = context.FindSigner(from.Address)
                         ?? throw new SecureMimeException(F("Kein gültiges eigenes S/MIME-Zertifikat für {0} vorhanden.", from.Address));
            body = await MultipartSigned.CreateAsync(context, signer, body, cancellationToken);
        }

        if (encrypt)
        {
            // The sender is always a recipient too, otherwise the copy in "Sent" could not be read.
            var addresses = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).Append(from)
                .Select(m => m.Address)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var recipients = new CmsRecipientCollection();
            var missing = new List<string>();
            foreach (var address in addresses)
            {
                if (context.FindRecipient(address) is { } recipient)
                {
                    recipients.Add(recipient);
                }
                else
                {
                    missing.Add(address);
                }
            }

            if (missing.Count > 0)
            {
                throw new SecureMimeException(T("Für folgende Empfänger ist kein gültiges Zertifikat vorhanden: ") + string.Join(", ", missing));
            }

            body = await ApplicationPkcs7Mime.EncryptAsync(context, recipients, body, cancellationToken);
        }

        message.Body = body;
    }

    /// <summary>Decrypts and verifies; signer certificates of good signatures are collected for later encryption.</summary>
    public async Task<OpenedMessage> OpenAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsSecure(message.Body))
        {
            return new OpenedMessage(message, MessageSecurityInfo.None);
        }

        using var context = await CreateContextAsync(cancellationToken);
        var entity = message.Body;
        var signatures = new List<SignatureInfo>();
        var encrypted = false;
        string? decryptionError = null;
        var sender = message.From.Mailboxes.FirstOrDefault()?.Address;

        for (var depth = 0; depth < 5 && entity is not null; depth++)
        {
            if (entity is MultipartSigned signed)
            {
                var collection = await signed.VerifyAsync(context, cancellationToken);
                signatures.AddRange(await EvaluateAsync(collection, sender, cancellationToken));
                entity = signed.Count > 0 ? signed[0] : null;
            }
            else if (entity is ApplicationPkcs7Mime pkcs7 && pkcs7.SecureMimeType == SecureMimeType.EnvelopedData)
            {
                encrypted = true;
                try
                {
                    entity = await pkcs7.DecryptAsync(context, cancellationToken);
                }
                catch (Exception ex) when (ex is Org.BouncyCastle.Cms.CmsException or PrivateKeyNotFoundException or CertificateNotFoundException or ArgumentException or InvalidCastException)
                {
                    logger.LogInformation(ex, "Could not decrypt S/MIME message");
                    decryptionError = ex is PrivateKeyNotFoundException or CertificateNotFoundException
                        ? T("Kein passender privater Schlüssel vorhanden. Bitte das eigene Zertifikat (.p12/.pfx) unter Einstellungen → Zertifikate importieren.")
                        : T("Die Nachricht konnte nicht entschlüsselt werden: ") + ex.Message;
                    break;
                }
            }
            else if (entity is ApplicationPkcs7Mime opaque && opaque.SecureMimeType == SecureMimeType.SignedData)
            {
                var collection = opaque.Verify(context, out var inner, cancellationToken);
                signatures.AddRange(await EvaluateAsync(collection, sender, cancellationToken));
                entity = inner;
            }
            else if (entity is ApplicationPkcs7Mime compressed && compressed.SecureMimeType == SecureMimeType.CompressedData)
            {
                entity = compressed.Decompress(context, cancellationToken);
            }
            else
            {
                break;
            }
        }

        var readable = decryptionError is null && entity is not null ? WithBody(message, entity) : message;
        return new OpenedMessage(readable, new MessageSecurityInfo(encrypted, decryptionError, signatures));
    }

    private static bool IsSecure(MimeEntity? body) => body is MultipartSigned or ApplicationPkcs7Mime;

    private static MimeMessage WithBody(MimeMessage original, MimeEntity body)
    {
        // Copy headers by round-tripping, then swap the body for the unwrapped one.
        using var buffer = new MemoryStream();
        original.WriteTo(buffer);
        buffer.Position = 0;
        var copy = MimeMessage.Load(buffer);
        copy.Body = body;
        return copy;
    }

    private async Task<List<SignatureInfo>> EvaluateAsync(DigitalSignatureCollection signatures, string? sender, CancellationToken cancellationToken)
    {
        var result = new List<SignatureInfo>();
        foreach (var signature in signatures)
        {
            var certificate = (signature.SignerCertificate as SecureMimeDigitalCertificate)?.Certificate;
            var emails = certificate is null ? [] : CertificateParser.EmailAddresses(certificate);
            var name = certificate is null ? T("Unbekannt") : CertificateParser.Describe(certificate, false, CertificateSource.CollectedFromMail, clock.GetUtcNow()).SubjectName;
            var issuer = certificate is null ? null : CertificateParser.Describe(certificate, false, CertificateSource.CollectedFromMail, clock.GetUtcNow()).IssuerName;

            SignatureStatus status;
            string? detail = null;
            try
            {
                status = signature.Verify() ? SignatureStatus.Valid : SignatureStatus.Invalid;
            }
            catch (DigitalSignatureVerifyException chainError)
            {
                try
                {
                    status = signature.Verify(verifySignatureOnly: true) ? SignatureStatus.ValidUntrusted : SignatureStatus.Invalid;
                    detail = chainError.Message;
                }
                catch (DigitalSignatureVerifyException ex)
                {
                    status = SignatureStatus.Invalid;
                    detail = ex.Message;
                }
            }

            var matches = sender is not null && emails.Contains(sender, StringComparer.OrdinalIgnoreCase);
            result.Add(new SignatureInfo(name, emails, issuer, status, matches, signature.CreationDate == default ? null : new DateTimeOffset(signature.CreationDate, TimeSpan.Zero), detail));

            if (status != SignatureStatus.Invalid && certificate is not null && await certificates.CollectAsync(certificate, cancellationToken))
            {
                logger.LogInformation("Collected S/MIME certificate of {Signer}", string.Join(", ", emails));
            }
        }

        return result;
    }

    private async Task<NerunaSecureMimeContext> CreateContextAsync(CancellationToken cancellationToken) =>
        new(await certificates.LoadMaterialAsync(cancellationToken), clock.GetUtcNow(),
            SecureMimeAlgorithms.Digest(await settings.GetAsync(SettingKeys.SmimeDigest, cancellationToken)),
            SecureMimeAlgorithms.Cipher(await settings.GetAsync(SettingKeys.SmimeCipher, cancellationToken)));
}

/// <summary>
/// MimeKit context backed by Neruna's certificate store. Chooses the newest valid certificate with the right
/// key usage per address, trusts the OS root store plus CAs the user imported, and does no online revocation checks.
/// </summary>
internal sealed class NerunaSecureMimeContext : TemporarySecureMimeContext
{
    private static readonly Lazy<List<BcCertificate>> SystemRoots = new(LoadSystemRoots);

    private readonly SecureMimeMaterial _material;
    private readonly DateTimeOffset _now;
    private readonly DigestAlgorithm _digest;
    private readonly EncryptionAlgorithm _cipher;

    public NerunaSecureMimeContext(SecureMimeMaterial material, DateTimeOffset now, DigestAlgorithm digest = DigestAlgorithm.Sha256, EncryptionAlgorithm cipher = EncryptionAlgorithm.Aes256)
    {
        _material = material;
        _now = now;
        _digest = digest;
        _cipher = cipher;
        CheckCertificateRevocation = false;

        foreach (var certificate in material.Certificates.Concat(material.PrivateKeys.SelectMany(k => k.Chain)))
        {
            Import(certificate);
        }
    }

    public CmsSigner? FindSigner(string address)
    {
        var info = Best(c => c.HasPrivateKey && c.CanSign && c.Covers(address));
        var entry = info is null ? null : _material.PrivateKeys.FirstOrDefault(k => CertificateParser.Thumbprint(k.Certificate) == info.Thumbprint);
        return entry is null ? null : new CmsSigner(entry.Chain, entry.PrivateKey) { DigestAlgorithm = _digest };
    }

    public CmsRecipient? FindRecipient(string address)
    {
        var info = Best(c => c.CanEncrypt && c.Covers(address));
        var certificate = info is null ? null : _material.Certificates.FirstOrDefault(c => CertificateParser.Thumbprint(c) == info.Thumbprint);
        return certificate is null ? null : new CmsRecipient(certificate) { EncryptionAlgorithms = [_cipher] };
    }

    protected override CmsSigner GetCmsSigner(MailboxAddress mailbox, DigestAlgorithm digestAlgo)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return FindSigner(mailbox.Address) ?? throw new CertificateNotFoundException(mailbox, T("Kein Signaturzertifikat gefunden."));
    }

    protected override CmsRecipient GetCmsRecipient(MailboxAddress mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return FindRecipient(mailbox.Address) ?? throw new CertificateNotFoundException(mailbox, T("Kein Verschlüsselungszertifikat gefunden."));
    }

    // MimeKit asks once per recipient of an encrypted message and expects null for "not mine" so it can try the
    // next recipient; throwing here would make our own copy in "Sent" unreadable when we are not listed first.
    protected override AsymmetricKeyParameter GetPrivateKey(ISelector<BcCertificate> selector) =>
        _material.PrivateKeys.FirstOrDefault(k => selector?.Match(k.Certificate) ?? false)?.PrivateKey
        ?? base.GetPrivateKey(selector)!;

    protected override ISet<TrustAnchor> GetTrustedAnchors()
    {
        var anchors = new HashSet<TrustAnchor>(base.GetTrustedAnchors());
        foreach (var root in SystemRoots.Value.Concat(_material.Authorities))
        {
            anchors.Add(new TrustAnchor(root, null));
        }

        return anchors;
    }

    private CertificateInfo? Best(Func<CertificateInfo, bool> predicate) =>
        _material.Infos.Where(c => c.IsUsableAt(_now) && predicate(c)).OrderByDescending(c => c.NotAfter).FirstOrDefault();

    // The OS trust store (Windows/macOS stores, /etc/ssl on Linux), as BouncyCastle certificates.
    private static List<BcCertificate> LoadSystemRoots()
    {
        var parser = new Org.BouncyCastle.X509.X509CertificateParser();
        var roots = new List<BcCertificate>();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly);
                roots.AddRange(store.Certificates.Select(c => parser.ReadCertificate(c.RawData)));
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // Not every platform has every store.
            }
        }

        return roots;
    }
}
