using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Neruna.Core.Security;

/// <summary>
/// Which server certificates Neruna accepts for its own TLS connections (IMAP, SMTP, ManageSieve). Like browsers and
/// other mail programs, an unknown revocation status is no reason to refuse: certificate revocation lists or OCSP are
/// often not reachable – behind firewalls with TLS inspection, whose own certificate authority publishes none that
/// clients can reach. Everything else still has to be right: trusted issuer, host name, validity, and a certificate
/// known to be revoked is refused.
/// </summary>
public static class TlsCertificates
{
    private const X509ChainStatusFlags RevocationUnknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    /// <summary>
    /// Connection setting (value "true"): this connection accepts any server certificate – for test servers with a
    /// self-signed one. Set by the user per account (Konto bearbeiten); never for accounts from Neruna Cloud.
    /// </summary>
    public const string AcceptAnyCertificateKey = "tls.acceptAnyCertificate";

    /// <summary>
    /// Connection setting: the TLS client certificate shown to the server (a reverse proxy that demands one), as a
    /// <see cref="ClientCertificates"/> reference. Absent: none.
    /// </summary>
    public const string ClientCertificateKey = "tls.clientCertificate";

    public static string? ClientCertificateOf(IReadOnlyDictionary<string, string> settings) =>
        settings is not null && settings.TryGetValue(ClientCertificateKey, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// Connection setting: SHA-256 fingerprints (hex, comma-separated) of server certificates accepted as they are –
    /// e.g. the self-signed certificate of Proton Mail Bridge on this computer, remembered when the account was set up.
    /// Any other certificate gets the usual check.
    /// </summary>
    public const string PinnedCertificateKey = "tls.pinnedCertificate";

    /// <summary>SHA-256 fingerprint of a certificate as stored in <see cref="PinnedCertificateKey"/>.</summary>
    public static string Fingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
    }

    /// <summary>The check for a connection with these settings: any certificate, a pinned one, or the usual check.</summary>
    public static RemoteCertificateValidationCallback For(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (AcceptsAnyCertificate(settings))
        {
            return For(acceptAny: true);
        }

        var pinned = settings.TryGetValue(PinnedCertificateKey, out var value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        return pinned.Count == 0
            ? Validate
            : (sender, certificate, chain, errors) => (certificate is not null && pinned.Contains(Fingerprint(certificate))) || Validate(sender, certificate, chain, errors);
    }

    public static bool AcceptsAnyCertificate(IReadOnlyDictionary<string, string> settings) =>
        settings is not null && settings.TryGetValue(AcceptAnyCertificateKey, out var value) && value == "true";

    /// <summary>The check for a connection: the usual one, or none when the user chose so for this account.</summary>
#pragma warning disable CA5359 // Accepting any certificate is the user's explicit choice for this one account (test servers).
    public static RemoteCertificateValidationCallback For(bool acceptAny) => acceptAny ? (_, _, _, _) => true : Validate;
#pragma warning restore CA5359

    /// <summary>For <c>ServerCertificateValidationCallback</c> / <c>RemoteCertificateValidationCallback</c>.</summary>
    public static bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        IsAcceptable(errors, chain?.ChainStatus.Select(s => s.Status) ?? []);

    internal static bool IsAcceptable(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags> chainStatus) =>
        errors == SslPolicyErrors.None
        || (errors == SslPolicyErrors.RemoteCertificateChainErrors && chainStatus.All(status => (status & ~RevocationUnknown) == X509ChainStatusFlags.NoError));
}
