using System.Globalization;
using System.Text;

namespace Neruna.Core.Contacts;

/// <summary>
/// The editable part of a contact. <see cref="ToVCard"/> replaces only these properties in an existing card,
/// so photos, addresses, birthdays and vendor extensions are preserved.
/// </summary>
public sealed record ContactDraft(
    string? GivenName,
    string? FamilyName,
    string? Organization,
    string? Title,
    IReadOnlyList<ContactField> Emails,
    IReadOnlyList<ContactField> Phones,
    string? Note)
{
    private static readonly HashSet<string> ManagedProperties = ["FN", "N", "ORG", "TITLE", "EMAIL", "TEL", "NOTE", "BDAY", "REV", "PRODID"];

    public static ContactDraft Empty { get; } = new(null, null, null, null, [], [], null);

    /// <summary>The contact picture as read from the card.</summary>
    public ContactPhoto? Photo { get; init; }

    /// <summary>
    /// True when the user chose a new picture or removed it: then <see cref="Photo"/> is written (or the PHOTO
    /// removed). Otherwise the card's PHOTO lines stay exactly as they are (also linked ones Neruna cannot show).
    /// </summary>
    public bool ReplacePhoto { get; init; }

    public ContactBirthday? Birthday { get; init; }

    /// <summary>
    /// S/MIME certificates (DER) to keep in the card as KEY; null leaves the card's certificates as they are (only an
    /// editor that changed them sets this).
    /// </summary>
    public IReadOnlyList<byte[]>? Certificates { get; init; }

    /// <summary>Public PGP keys to keep in the card as KEY; null leaves the card's keys as they are.</summary>
    public IReadOnlyList<byte[]>? PgpKeys { get; init; }

    /// <summary>Postal addresses to write as ADR (replacing the card's ADR and LABEL); null leaves them as they are.</summary>
    public IReadOnlyList<ContactAddress>? Addresses { get; init; }

    /// <summary>Web addresses to write as URL (replacing the card's); null leaves them as they are.</summary>
    public IReadOnlyList<ContactField>? Websites { get; init; }

    public string DisplayName
    {
        get
        {
            var name = $"{GivenName} {FamilyName}".Trim();
            if (name.Length > 0)
            {
                return name;
            }

            return !string.IsNullOrWhiteSpace(Organization) ? Organization.Trim() : Emails.FirstOrDefault()?.Value ?? string.Empty;
        }
    }

    public static ContactDraft FromVCard(string vcard)
    {
        var card = VCardReader.Read(vcard);
        return new ContactDraft(card.GivenName, card.FamilyName, card.Organization, card.Title, card.Emails, card.Phones, card.Note) { Photo = card.Photo, Birthday = card.Birthday };
    }

    /// <summary>
    /// Like <see cref="FromVCard"/>, with the addresses and web addresses too – for an editor that shows them (saving
    /// then writes them as edited).
    /// </summary>
    public static ContactDraft FromVCardWithAddresses(string vcard)
    {
        var card = VCardReader.Read(vcard);
        return FromVCard(vcard) with { Addresses = card.Addresses, Websites = card.Websites };
    }

    /// <summary>
    /// This contact completed with <paramref name="extra"/> (e.g. the sender of a mail and what its signature shows):
    /// e-mail addresses, phone numbers, postal and web addresses not there yet are added; name, organisation and title
    /// only fill empty fields. Nothing is replaced or removed.
    /// </summary>
    public ContactDraft CompletedWith(ContactDraft extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var emails = Emails.ToList();
        emails.AddRange(extra.Emails.Where(e => !string.IsNullOrWhiteSpace(e.Value)
            && !emails.Any(k => string.Equals(k.Value.Trim(), e.Value.Trim(), StringComparison.OrdinalIgnoreCase))));
        var phones = Phones.ToList();
        phones.AddRange(extra.Phones.Where(p => !string.IsNullOrWhiteSpace(p.Value) && !phones.Any(k => SamePhone(k.Value, p.Value))));

        var addresses = Addresses;
        if (extra.Addresses is { Count: > 0 } newAddresses)
        {
            var all = (Addresses ?? []).ToList();
            all.AddRange(newAddresses.Where(a => !a.IsEmpty && !all.Any(k => k.SameAs(a))));
            addresses = all;
        }

        var websites = Websites;
        if (extra.Websites is { Count: > 0 } newSites)
        {
            var all = (Websites ?? []).ToList();
            all.AddRange(newSites.Where(w => !string.IsNullOrWhiteSpace(w.Value) && !all.Any(k => SameSite(k.Value, w.Value))));
            websites = all;
        }

        var noName = string.IsNullOrWhiteSpace(GivenName) && string.IsNullOrWhiteSpace(FamilyName);
        return this with
        {
            GivenName = noName ? extra.GivenName : GivenName,
            FamilyName = noName ? extra.FamilyName : FamilyName,
            Organization = string.IsNullOrWhiteSpace(Organization) ? extra.Organization : Organization,
            Title = string.IsNullOrWhiteSpace(Title) ? extra.Title : Title,
            Emails = emails,
            Phones = phones,
            Addresses = addresses,
            Websites = websites,
        };
    }

    /// <summary>The same number written differently: "+41 44 123 45 67", "044 123 45 67", "0041441234567".</summary>
    public static bool SamePhone(string a, string b)
    {
        static string Digits(string value) => string.Concat(value.Where(char.IsAsciiDigit));
        var x = Digits(a);
        var y = Digits(b);
        if (x.Length < 6 || y.Length < 6)
        {
            return x == y;
        }

        // Without the country code and the leading 0 of the area code the last nine digits are the same.
        var length = Math.Min(9, Math.Min(x.Length, y.Length));
        return x[^length..] == y[^length..];
    }

    private static bool SameSite(string a, string b)
    {
        static string Normal(string value)
        {
            var v = value.Trim().ToLowerInvariant();
            v = v.StartsWith("https://", StringComparison.Ordinal) ? v[8..] : v.StartsWith("http://", StringComparison.Ordinal) ? v[7..] : v;
            v = v.StartsWith("www.", StringComparison.Ordinal) ? v[4..] : v;
            return v.TrimEnd('/');
        }

        return Normal(a) == Normal(b);
    }

    /// <summary>Returns a complete vCard: <paramref name="existing"/> updated, or a new vCard 3.0.</summary>
    public string ToVCard(string? existing)
    {
        var kept = new List<string>();
        var version = "3.0";
        string? uid = null;

        if (existing is not null)
        {
            foreach (var property in VCardProperty.Parse(existing))
            {
                switch (property.Name)
                {
                    case "BEGIN" or "END":
                        continue;
                    case "VERSION":
                        version = property.Value.Trim();
                        continue;
                    case "UID":
                        uid = property.Value.Trim();
                        continue;
                }

                if (!ManagedProperties.Contains(property.Name) && !(ReplacePhoto && property.Name == "PHOTO")
                    && !(Addresses is not null && property.Name is "ADR" or "LABEL") && !(Websites is not null && property.Name == "URL"))
                {
                    kept.Add(property.RawLine);
                }
            }
        }

        var v4 = version.StartsWith('4');
        var lines = new List<string>
        {
            "BEGIN:VCARD",
            "VERSION:" + version,
            "PRODID:-//Neruna//Neruna Desktop//DE",
            "UID:" + (uid ?? (v4 ? "urn:uuid:" : string.Empty) + Guid.NewGuid().ToString("D")),
            "FN:" + VCardWriter.Escape(DisplayName),
            $"N:{VCardWriter.Escape(FamilyName)};{VCardWriter.Escape(GivenName)};;;",
        };

        if (!string.IsNullOrWhiteSpace(Organization))
        {
            lines.Add("ORG:" + VCardWriter.Escape(Organization.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(Title))
        {
            lines.Add("TITLE:" + VCardWriter.Escape(Title.Trim()));
        }

        foreach (var email in Emails.Where(e => !string.IsNullOrWhiteSpace(e.Value)))
        {
            var type = v4 ? TypeParameter(email.Kind) : TypeParameter(email.Kind is null ? "internet" : "internet," + email.Kind);
            lines.Add($"EMAIL{type}:{VCardWriter.Escape(email.Value.Trim())}");
        }

        foreach (var phone in Phones.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
        {
            var value = v4 ? ";VALUE=text" : string.Empty;
            lines.Add($"TEL{value}{TypeParameter(phone.Kind)}:{VCardWriter.Escape(phone.Value.Trim())}");
        }

        foreach (var address in (Addresses ?? []).Where(a => !a.IsEmpty))
        {
            lines.Add($"ADR{TypeParameter(address.Kind)}:" + string.Join(';', new[]
            {
                address.PoBox, address.Extended, address.Street, address.City, address.Region, address.PostalCode, address.Country,
            }.Select(part => VCardWriter.Escape(part?.Trim()))));
        }

        foreach (var site in (Websites ?? []).Where(w => !string.IsNullOrWhiteSpace(w.Value)))
        {
            lines.Add($"URL{TypeParameter(site.Kind)}:{site.Value.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(Note))
        {
            lines.Add("NOTE:" + VCardWriter.Escape(Note.Trim()));
        }

        if (Birthday is { } birthday)
        {
            // "--MMDD" (no year) is a valid date-and-or-time value in vCard 4 and widely read in vCard 3.
            lines.Add("BDAY:" + birthday.ToVCardValue(v4));
        }

        if (ReplacePhoto && Photo is { } photo)
        {
            var base64 = Convert.ToBase64String(photo.Data);
            lines.Add(v4
                ? $"PHOTO:data:{photo.MediaType};base64,{base64}"
                : $"PHOTO;ENCODING=b;TYPE={photo.MediaType.Split('/').Last().ToUpperInvariant()}:{base64}");
        }

        lines.AddRange(kept);
        lines.Add("REV:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        lines.Add("END:VCARD");

        var result = new StringBuilder();
        foreach (var line in lines)
        {
            VCardWriter.Fold(result, line);
        }

        var vcard = Certificates is null ? result.ToString() : ContactCertificates.WithAll(result.ToString(), Certificates);
        return PgpKeys is null ? vcard : ContactPgpKeys.WithAll(vcard, PgpKeys);
    }

    private static string TypeParameter(string? kind) => string.IsNullOrWhiteSpace(kind) ? string.Empty : ";TYPE=" + kind.ToUpperInvariant();
}
