using System.Globalization;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>One line of the certificate details: what and its value.</summary>
internal sealed record DetailRow(string Label, string Value);

/// <summary>
/// The info button in the status of a signed mail: whose certificate (or PGP key) signed it, by whom it was issued,
/// how long it is valid, its serial number and fingerprints – without going to the contact.
/// </summary>
internal sealed record SignerDetails(string Title, IReadOnlyList<DetailRow> Rows)
{
    public static SignerDetails From(SignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var rows = new List<DetailRow>();
        if (signature.SignerEmails.Count > 0)
        {
            rows.Add(new(T("E-Mail"), string.Join(", ", signature.SignerEmails)));
        }

        if (signature.IsPgp)
        {
            if (!string.IsNullOrEmpty(signature.IssuerName))
            {
                rows.Add(new(T("Fingerabdruck"), signature.IssuerName));
            }
        }
        else if (Parse(signature.Certificate) is { } certificate)
        {
            var info = CertificateParser.Describe(certificate, false, CertificateSource.CollectedFromMail, DateTimeOffset.Now);
            rows.Add(new(T("Ausgestellt von"), info.IssuerName));
            rows.Add(new(T("Gültig ab"), Date(info.NotBefore)));
            rows.Add(new(T("Gültig bis"), Date(info.NotAfter) + info.StatusAt(DateTimeOffset.Now) switch
            {
                CertificateStatus.Expired => " – " + T("abgelaufen"),
                CertificateStatus.NotYetValid => " – " + T("noch nicht gültig"),
                _ => string.Empty,
            }));
            rows.Add(new(T("Verwendung"), string.Join(", ", new[] { info.CanSign ? T("Signieren") : null, info.CanEncrypt ? T("Verschlüsseln") : null }.OfType<string>())));
            rows.Add(new(T("Seriennummer"), certificate.SerialNumber.ToString(16).ToUpperInvariant()));
            rows.Add(new(T("Fingerabdruck (SHA-256)"), Grouped(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.GetEncoded())))));
            rows.Add(new(T("Fingerabdruck (SHA-1)"), Grouped(info.Thumbprint)));
            rows.Add(new(T("Inhaber (vollständig)"), certificate.SubjectDN.ToString()));
            rows.Add(new(T("Aussteller (vollständig)"), certificate.IssuerDN.ToString()));
        }
        else if (!string.IsNullOrEmpty(signature.IssuerName))
        {
            rows.Add(new(T("Ausgestellt von"), signature.IssuerName));
        }

        if (signature.SignedAt is { } signedAt)
        {
            rows.Add(new(T("Signiert am"), signedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        }

        return new SignerDetails(signature.SignerName, rows);
    }

    private static Org.BouncyCastle.X509.X509Certificate? Parse(byte[]? der)
    {
        if (der is null)
        {
            return null;
        }

        try
        {
            return new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(der);
        }
        catch (Exception ex) when (ex is Org.BouncyCastle.Security.Certificates.CertificateException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("d. MMMM yyyy", CultureInfo.CurrentCulture);

    // "AB12 CD34 …": easier to compare.
    private static string Grouped(string hex) => string.Join(' ', Enumerable.Range(0, (hex.Length + 3) / 4).Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))));
}
