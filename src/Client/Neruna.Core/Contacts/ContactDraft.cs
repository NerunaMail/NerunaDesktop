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

                if (!ManagedProperties.Contains(property.Name) && !(ReplacePhoto && property.Name == "PHOTO"))
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

        return result.ToString();
    }

    private static string TypeParameter(string? kind) => string.IsNullOrWhiteSpace(kind) ? string.Empty : ";TYPE=" + kind.ToUpperInvariant();
}
