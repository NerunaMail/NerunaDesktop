using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Security;

/// <summary>A certificate Neruna can show to a server that asks for one (TLS client authentication).</summary>
/// <param name="Reference">What a connection stores: the thumbprint, or <c>system:</c> + thumbprint for one of the system store.</param>
public sealed record ClientCertificateInfo(string Reference, string Subject, string Issuer, DateTimeOffset NotAfter, bool IsFromSystem)
{
    /// <summary>For lists: "Anna Muster (Muster AG CA) · bis 31.12.2027".</summary>
    public string DisplayName => F("{0} ({1}) · bis {2:d}", Subject, Issuer, NotAfter.LocalDateTime);
}

/// <summary>
/// TLS client certificates – typically demanded by a reverse proxy in front of Exchange. An imported PKCS#12 file stays
/// encrypted with its password; both lie in the keychain (one secret per certificate, never in SQLite). On Windows and
/// macOS a certificate of the system store (with private key; smart cards too) can be used directly instead.
/// A connection refers to its certificate by <see cref="TlsCertificates.ClientCertificateKey"/>.
/// </summary>
public sealed class ClientCertificates(ICredentialStore credentials)
{
    public const string SystemPrefix = "system:";

    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    private sealed record Stored(string Pkcs12, string Password);

    /// <summary>Takes the certificate with private key from a .p12/.pfx file and keeps it in the keychain.</summary>
    /// <exception cref="CertificatePasswordException">Wrong password.</exception>
    /// <exception cref="FormatException">No certificate with private key in the file.</exception>
    public async Task<ClientCertificateInfo> ImportAsync(byte[] pkcs12, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);
        using var certificate = Load(pkcs12, password ?? string.Empty);
        var thumbprint = certificate.Thumbprint;
        var stored = JsonSerializer.Serialize(new Stored(Convert.ToBase64String(pkcs12), password ?? string.Empty));
        await credentials.SetSecretAsync(SecretId(thumbprint), stored, cancellationToken);
        return Describe(certificate, thumbprint, false);
    }

    /// <summary>The certificate for a connection, with private key; null if it is gone (removed, other computer).</summary>
    public async Task<X509Certificate2?> LoadAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.StartsWith(SystemPrefix, StringComparison.Ordinal))
        {
            return FromSystemStore(reference[SystemPrefix.Length..]);
        }

        if (await credentials.GetSecretAsync(SecretId(reference), cancellationToken) is not { } secret
            || JsonSerializer.Deserialize<Stored>(secret) is not { } stored)
        {
            return null;
        }

        return Load(Convert.FromBase64String(stored.Pkcs12), stored.Password);
    }

    /// <summary>Name, issuer and expiry for the account dialog; null if the certificate is not available here.</summary>
    public async Task<ClientCertificateInfo?> DescribeAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var certificate = await LoadAsync(reference, cancellationToken);
        return certificate is null ? null : Describe(certificate, reference, reference.StartsWith(SystemPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Certificates of the user's system store that can authenticate a client (private key, valid, no other purpose
    /// only). Windows: "Eigene Zertifikate"; macOS: the login keychain. Linux has no such store: empty.
    /// </summary>
    public static IReadOnlyList<ClientCertificateInfo> SystemCertificates()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return [];
        }

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var now = DateTime.Now;
            var result = new List<ClientCertificateInfo>();
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    if (certificate.HasPrivateKey && certificate.NotBefore <= now && certificate.NotAfter > now && AuthenticatesClients(certificate))
                    {
                        result.Add(Describe(certificate, SystemPrefix + certificate.Thumbprint, true));
                    }
                }
            }

            return [.. result.OrderBy(c => c.Subject, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (CryptographicException)
        {
            return []; // no store for this user
        }
    }

    /// <summary>Removes an imported certificate from the keychain (system certificates stay where they are).</summary>
    public Task RemoveAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith(SystemPrefix, StringComparison.Ordinal)
            ? Task.CompletedTask
            : credentials.DeleteSecretAsync(SecretId(reference), cancellationToken);
    }

    internal static bool AuthenticatesClients(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is null || usage.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value is ClientAuthentication or "2.5.29.37.0");
    }

    private static X509Certificate2 Load(byte[] pkcs12, string password)
    {
        X509Certificate2Collection all;
        try
        {
            // Not ephemeral: Windows' TLS (SChannel) cannot use an ephemeral key for client authentication.
            all = X509CertificateLoader.LoadPkcs12Collection(pkcs12, password, X509KeyStorageFlags.UserKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new CertificatePasswordException(T("Das Passwort des Zertifikats ist falsch, oder die Datei ist kein PKCS#12-Container (.p12/.pfx)."), ex);
        }

        var chosen = all.FirstOrDefault(c => c.HasPrivateKey && AuthenticatesClients(c)) ?? all.FirstOrDefault(c => c.HasPrivateKey);
        foreach (var other in all.Where(c => !ReferenceEquals(c, chosen)))
        {
            other.Dispose();
        }

        return chosen ?? throw new FormatException(T("Die Datei enthält kein Zertifikat mit privatem Schlüssel."));
    }

    private static X509Certificate2? FromSystemStore(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            var certificate = found.FirstOrDefault(c => c.HasPrivateKey);
            foreach (var other in found.Where(c => !ReferenceEquals(c, certificate)))
            {
                other.Dispose();
            }

            return certificate;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    internal static ClientCertificateInfo Describe(X509Certificate2 certificate, string reference, bool fromSystem) =>
        new(reference, NameOf(certificate, issuer: false), NameOf(certificate, issuer: true), new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero), fromSystem);

    /// <summary>The common name (also within a multi-valued part like "CN=Anna+OU=IT"), else the whole name.</summary>
    private static string NameOf(X509Certificate2 certificate, bool issuer)
    {
        var simple = certificate.GetNameInfo(X509NameType.SimpleName, issuer);
        return string.IsNullOrWhiteSpace(simple) ? (issuer ? certificate.Issuer : certificate.Subject) : simple;
    }

    private static Guid SecretId(string thumbprint) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("neruna-client-cert:" + thumbprint.ToUpperInvariant())).AsSpan(0, 16));
}
