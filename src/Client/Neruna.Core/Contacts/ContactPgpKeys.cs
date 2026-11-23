namespace Neruna.Core.Contacts;

/// <summary>
/// Public PGP keys (binary key ring) in a vCard: <c>KEY</c> – vCard 4.0 as <c>data:application/pgp-keys;base64,…</c>,
/// 3.0 as <c>KEY;ENCODING=b;TYPE=PGP:…</c>, as other address books write them. Exchange and Microsoft 365 do not keep them.
/// </summary>
public static class ContactPgpKeys
{
    /// <summary>The PGP keys of a vCard (certificates and URLs are left out).</summary>
    public static IReadOnlyList<byte[]> Read(string vcard)
    {
        ArgumentNullException.ThrowIfNull(vcard);
        var result = new List<byte[]>();
        foreach (var property in VCardProperty.Parse(vcard).Where(p => p.Name == "KEY"))
        {
            if (Decode(property) is { } key && !result.Any(k => k.AsSpan().SequenceEqual(key)))
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>The vCard with <paramref name="key"/> added (unchanged if it is there already).</summary>
    public static string Add(string vcard, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(vcard);
        ArgumentNullException.ThrowIfNull(key);
        if (Read(vcard).Any(k => k.AsSpan().SequenceEqual(key)))
        {
            return vcard;
        }

        var base64 = Convert.ToBase64String(key);
        var version4 = VCardProperty.Parse(vcard).FirstOrDefault(p => p.Name == "VERSION")?.Value.Trim() == "4.0";
        var line = version4 ? "KEY:data:application/pgp-keys;base64," + base64 : "KEY;ENCODING=b;TYPE=PGP:" + base64;
        var lines = VCardProperty.Unfold(vcard).Where(l => l.Length > 0).ToList();
        var end = lines.FindLastIndex(l => l.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase));
        lines.Insert(end < 0 ? lines.Count : end, line);
        return VCardWriter.Join(lines);
    }

    /// <summary>The vCard without <paramref name="key"/> (other keys and certificates stay).</summary>
    public static string Remove(string vcard, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(vcard);
        ArgumentNullException.ThrowIfNull(key);
        if (!Read(vcard).Any(k => k.AsSpan().SequenceEqual(key)))
        {
            return vcard;
        }

        return VCardWriter.Join(VCardProperty.Unfold(vcard).Where(l => l.Length > 0)
            .Where(l => !(VCardProperty.Parse(l).FirstOrDefault() is { Name: "KEY" } p && Decode(p) is { } k && k.AsSpan().SequenceEqual(key))));
    }

    /// <summary>The vCard with exactly these PGP keys (other KEY entries stay).</summary>
    public static string WithAll(string vcard, IEnumerable<byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = Read(vcard).Aggregate(vcard, Remove);
        return keys.Aggregate(result, Add);
    }

    private static byte[]? Decode(VCardProperty property)
    {
        try
        {
            var value = property.Value.Trim();
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = value.IndexOf(',', StringComparison.Ordinal);
                var header = comma < 0 ? string.Empty : value[5..comma].ToLowerInvariant();
                if (comma < 0 || !(header.StartsWith("application/pgp-keys", StringComparison.Ordinal) || header.StartsWith("application/pgp", StringComparison.Ordinal)))
                {
                    return null;
                }

                var data = value[(comma + 1)..];
                return header.Contains("base64", StringComparison.Ordinal)
                    ? Convert.FromBase64String(data)
                    : System.Text.Encoding.ASCII.GetBytes(Uri.UnescapeDataString(data));
            }

            // vCard 3.0: binary with TYPE=PGP (or a MEDIATYPE naming PGP keys).
            var type = property.Parameter("TYPE") ?? property.Parameter("MEDIATYPE")
                       ?? property.Parameters.Split(';').Select(p => p.Trim()).FirstOrDefault(p => !p.Contains('=', StringComparison.Ordinal));
            var encoding = property.Parameter("ENCODING");
            var binary = encoding is not null && (encoding.Equals("b", StringComparison.OrdinalIgnoreCase) || encoding.Equals("BASE64", StringComparison.OrdinalIgnoreCase))
                         || property.Parameters.Split(';').Any(p => p.Trim().Equals("BASE64", StringComparison.OrdinalIgnoreCase));
            return binary && type is not null && type.Contains("PGP", StringComparison.OrdinalIgnoreCase) && value.Length > 0 ? Convert.FromBase64String(value) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
