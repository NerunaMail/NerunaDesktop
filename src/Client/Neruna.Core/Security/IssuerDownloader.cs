using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using BcCertificate = Org.BouncyCastle.X509.X509Certificate;

namespace Neruna.Core.Security;

/// <summary>
/// Intermediate certificates a signature leaves out (some send only the signer's own certificate): fetched from the
/// address the certificate names for its issuer ("Authority Information Access", as Windows does). Only the chain gets
/// longer – whether it ends at a trusted root is still decided by the trust store.
/// </summary>
internal sealed class IssuerDownloader(HttpClient http)
{
    private const int MaxHops = 3;
    private const int MaxBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Per address, for the whole run: also a failed download (empty), so an unreachable CA is not asked for every mail.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<BcCertificate>> Cache = new(StringComparer.Ordinal);

    /// <summary>The issuers above <paramref name="certificate"/>, as far as they can be fetched.</summary>
    public async Task<IReadOnlyList<BcCertificate>> FetchAsync(BcCertificate certificate, CancellationToken cancellationToken)
    {
        var result = new List<BcCertificate>();
        var current = certificate;
        for (var hop = 0; hop < MaxHops && !current.IssuerDN.Equivalent(current.SubjectDN); hop++)
        {
            var issuer = (await DownloadAsync(current, cancellationToken)).FirstOrDefault(c => c.SubjectDN.Equivalent(current.IssuerDN));
            if (issuer is null || result.Any(r => r.Equals(issuer)))
            {
                break;
            }

            result.Add(issuer);
            current = issuer;
        }

        return result;
    }

    private async Task<IReadOnlyList<BcCertificate>> DownloadAsync(BcCertificate certificate, CancellationToken cancellationToken)
    {
        var found = new List<BcCertificate>();
        foreach (var url in IssuerUrls(certificate))
        {
            if (!Cache.TryGetValue(url, out var certificates))
            {
                certificates = await TryDownloadAsync(url, cancellationToken);
                Cache[url] = certificates;
            }

            found.AddRange(certificates);
        }

        return found;
    }

    private async Task<IReadOnlyList<BcCertificate>> TryDownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return [];
            }

            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (data.Length > MaxBytes)
            {
                return [];
            }

            // A single certificate (DER or PEM) or a "certs-only" PKCS#7 bundle (.p7c).
            return [.. new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificates(data)];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Org.BouncyCastle.Security.Certificates.CertificateException or IOException or ArgumentException)
        {
            return [];
        }
    }

    internal static IEnumerable<string> IssuerUrls(BcCertificate certificate)
    {
        var extension = certificate.GetExtensionValue(X509Extensions.AuthorityInfoAccess);
        if (extension is null)
        {
            return [];
        }

        var access = AuthorityInformationAccess.GetInstance(Asn1Object.FromByteArray(extension.GetOctets()));
        return access.GetAccessDescriptions()
            .Where(d => d.AccessMethod.Equals(AccessDescription.IdADCAIssuers) && d.AccessLocation.TagNo == GeneralName.UniformResourceIdentifier)
            .Select(d => DerIA5String.GetInstance(d.AccessLocation.Name).GetString())
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
