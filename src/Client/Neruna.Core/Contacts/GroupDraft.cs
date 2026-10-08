using System.Globalization;

namespace Neruna.Core.Contacts;

/// <summary>
/// A contact group (distribution list). Written like Apple Contacts and SOGo do it – vCard 3.0 with
/// X-ADDRESSBOOKSERVER-KIND:group and one X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:… per contact; existing vCard 4 groups
/// keep KIND:group/MEMBER. The address chosen for a member is stored as parameter X-NERUNA-EMAIL, which other clients
/// ignore (they use the contact's default address). Addresses without contact are mailto: members.
/// </summary>
public sealed record GroupDraft(string Name, IReadOnlyList<GroupMember> Members)
{
    public const string EmailParameter = "X-NERUNA-EMAIL";

    private static readonly HashSet<string> ManagedProperties =
        ["BEGIN", "END", "VERSION", "UID", "FN", "N", "KIND", "X-ADDRESSBOOKSERVER-KIND", "MEMBER", "X-ADDRESSBOOKSERVER-MEMBER", "REV", "PRODID"];

    public static GroupDraft FromVCard(string vcard)
    {
        var card = VCardReader.Read(vcard);
        return new GroupDraft(card.DisplayName, card.Members);
    }

    public string ToVCard(string? existing)
    {
        var kept = new List<string>();
        var version = "3.0";
        string? uid = null;
        var kindProperty = false;

        if (existing is not null)
        {
            foreach (var property in VCardProperty.Parse(existing))
            {
                switch (property.Name)
                {
                    case "VERSION":
                        version = property.Value.Trim();
                        break;
                    case "UID":
                        uid = property.Value.Trim();
                        break;
                    case "KIND":
                        kindProperty = true;
                        break;
                }

                if (!ManagedProperties.Contains(property.Name))
                {
                    kept.Add(property.RawLine);
                }
            }
        }

        // vCard 4 groups use the standard properties, everything else the Apple/SOGo ones.
        var standard = version.StartsWith('4') || kindProperty;
        var memberProperty = standard ? "MEMBER" : "X-ADDRESSBOOKSERVER-MEMBER";
        var lines = new List<string>
        {
            "BEGIN:VCARD",
            "VERSION:" + version,
            "PRODID:-//Neruna//Neruna Desktop//DE",
            "UID:" + (uid ?? Guid.NewGuid().ToString("D")),
            "FN:" + VCardWriter.Escape(Name.Trim()),
            "N:" + VCardWriter.Escape(Name.Trim()) + ";;;;",
            (standard ? "KIND" : "X-ADDRESSBOOKSERVER-KIND") + ":group",
        };

        foreach (var member in Members)
        {
            if (member.ContactUid is { } contactUid)
            {
                var email = string.IsNullOrWhiteSpace(member.Email) ? string.Empty : $";{EmailParameter}=\"{member.Email.Trim()}\"";
                lines.Add($"{memberProperty}{email}:urn:uuid:{contactUid}");
            }
            else if (!string.IsNullOrWhiteSpace(member.Email))
            {
                lines.Add($"{memberProperty}:mailto:{member.Email.Trim()}");
            }
        }

        lines.AddRange(kept);
        lines.Add("REV:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        lines.Add("END:VCARD");
        return VCardWriter.Join(lines);
    }
}
