namespace Neruna.Core.Contacts;

/// <summary>
/// S/MIME certificates (public part, DER) in a vCard: <c>KEY</c> – vCard 4.0 as <c>data:application/pkix-cert;base64,…</c>,
/// 3.0 as <c>KEY;ENCODING=b;TYPE=X509:…</c>. CardDAV servers keep it as it is; Microsoft 365 and Exchange keep a
/// contact's certificates in the MAPI property PidTagUserX509Certificate, which their providers map to KEY.
/// </summary>
public static class ContactCertificates
{
    /// <summary>The X.509 certificates of a vCard (PGP keys and URLs are left out).</summary>
    public static IReadOnlyList<byte[]> Read(string vcard)
    {
        ArgumentNullException.ThrowIfNull(vcard);
        var result = new List<byte[]>();
        foreach (var property in VCardProperty.Parse(vcard).Where(p => p.Name == "KEY"))
        {
            if (Decode(property) is { } der && !result.Any(c => c.AsSpan().SequenceEqual(der)))
            {
                result.Add(der);
            }
        }

        return result;
    }

    /// <summary>The vCard with <paramref name="der"/> added (unchanged if it is there already).</summary>
    public static string Add(string vcard, byte[] der)
    {
        ArgumentNullException.ThrowIfNull(vcard);
        ArgumentNullException.ThrowIfNull(der);
        if (Read(vcard).Any(c => c.AsSpan().SequenceEqual(der)))
        {
            return vcard;
        }

        var base64 = Convert.ToBase64String(der);
        var line = IsVersion4(vcard) ? "KEY:data:application/pkix-cert;base64," + base64 : "KEY;ENCODING=b;TYPE=X509:" + base64;
        var lines = VCardProperty.Unfold(vcard).Where(l => l.Length > 0).ToList();
        var end = lines.FindLastIndex(l => l.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase));
        lines.Insert(end < 0 ? lines.Count : end, line);
        return VCardWriter.Join(lines);
    }

    /// <summary>The vCard without <paramref name="der"/> (other keys stay).</summary>
    public static string Remove(string vcard, byte[] der)
    {
        ArgumentNullException.ThrowIfNull(vcard);
        ArgumentNullException.ThrowIfNull(der);
        var properties = VCardProperty.Parse(vcard).ToList();
        if (!properties.Any(p => p.Name == "KEY" && Decode(p) is { } c && c.AsSpan().SequenceEqual(der)))
        {
            return vcard;
        }

        return VCardWriter.Join(VCardProperty.Unfold(vcard).Where(l => l.Length > 0)
            .Where(l => !(VCardProperty.Parse(l).FirstOrDefault() is { Name: "KEY" } p && Decode(p) is { } c && c.AsSpan().SequenceEqual(der))));
    }

    /// <summary>The vCard with exactly these certificates (for providers that keep them elsewhere, e.g. Exchange).</summary>
    public static string WithAll(string vcard, IEnumerable<byte[]> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        var result = Read(vcard).Aggregate(vcard, Remove);
        return certificates.Aggregate(result, Add);
    }

    private static bool IsVersion4(string vcard) =>
        VCardProperty.Parse(vcard).FirstOrDefault(p => p.Name == "VERSION")?.Value.Trim() == "4.0";

    private static byte[]? Decode(VCardProperty property)
    {
        try
        {
            var value = property.Value.Trim();
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = value.IndexOf(',', StringComparison.Ordinal);
                var header = comma < 0 ? string.Empty : value[5..comma].ToLowerInvariant();
                return comma >= 0 && header.Contains("base64", StringComparison.Ordinal)
                       && (header.StartsWith("application/pkix-cert", StringComparison.Ordinal) || header.StartsWith("application/x-x509", StringComparison.Ordinal))
                    ? Convert.FromBase64String(value[(comma + 1)..])
                    : null;
            }

            // vCard 3.0: binary with ENCODING=b; a TYPE other than X.509 (PGP) is not a certificate.
            var encoding = property.Parameter("ENCODING");
            var type = property.Parameter("TYPE") ?? property.Parameters.Split(';').Select(p => p.Trim()).FirstOrDefault(p => !p.Contains('=', StringComparison.Ordinal));
            var binary = encoding is not null && (encoding.Equals("b", StringComparison.OrdinalIgnoreCase) || encoding.Equals("BASE64", StringComparison.OrdinalIgnoreCase))
                         || property.Parameters.Split(';').Any(p => p.Trim().Equals("BASE64", StringComparison.OrdinalIgnoreCase));
            var x509 = type is null || !type.Contains("PGP", StringComparison.OrdinalIgnoreCase);
            return binary && x509 && value.Length > 0 ? Convert.FromBase64String(value) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
