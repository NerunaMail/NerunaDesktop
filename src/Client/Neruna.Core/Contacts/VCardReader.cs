using System.Text;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Contacts;

/// <summary>An email address or phone number with its kind (vCard TYPE), e.g. "work", "home", "cell".</summary>
public sealed record ContactField(string Value, string? Kind = null);

public enum ContactKind
{
    Individual,

    /// <summary>A contact group / distribution list (vCard 4 KIND:group or Apple's X-ADDRESSBOOKSERVER-KIND:group).</summary>
    Group,
}

/// <summary>An embedded contact picture (vCard PHOTO).</summary>
public sealed record ContactPhoto(byte[] Data, string MediaType);

/// <summary>
/// One member of a group: a contact (by UID, optionally with the address to use for this list) or an external
/// address without contact.
/// </summary>
/// <param name="ContactUid">UID without "urn:uuid:", or null for an external address.</param>
/// <param name="Email">The address used in this group; for a contact null means "its first address".</param>
public sealed record GroupMember(string? ContactUid, string? Email)
{
    public bool IsExternal => ContactUid is null;
}

/// <summary>The fields Neruna displays and edits. The full vCard stays the source of truth.</summary>
public sealed record ContactCard(
    string Uid,
    string DisplayName,
    string? GivenName,
    string? FamilyName,
    string? Organization,
    string? Title,
    string? Note,
    IReadOnlyList<ContactField> Emails,
    IReadOnlyList<ContactField> Phones)
{
    public IReadOnlyList<string> EmailAddresses => Emails.Select(e => e.Value).ToList();

    public IReadOnlyList<string> PhoneNumbers => Phones.Select(p => p.Value).ToList();

    public ContactKind Kind { get; init; }

    public bool IsGroup => Kind == ContactKind.Group;

    public IReadOnlyList<GroupMember> Members { get; init; } = [];

    public ContactPhoto? Photo { get; init; }

    /// <summary>The UID as used in group member references ("urn:uuid:" stripped).</summary>
    public string MemberUid => NormalizeUid(Uid);

    public static string NormalizeUid(string uid)
    {
        ArgumentNullException.ThrowIfNull(uid);
        var trimmed = uid.Trim();
        return trimmed.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) ? trimmed[9..] : trimmed;
    }
}

/// <summary>Reader for vCard 3.0/4.0 (RFC 2426/6350): unfolding, groups, parameters and escaping.</summary>
public static class VCardReader
{
    public static ContactCard Read(string vcard)
    {
        ArgumentNullException.ThrowIfNull(vcard);
        string? uid = null, formattedName = null, given = null, family = null, organization = null, title = null, note = null;
        var emails = new List<ContactField>();
        var phones = new List<ContactField>();
        var members = new List<GroupMember>();
        var kind = ContactKind.Individual;
        ContactPhoto? photo = null;

        foreach (var property in VCardProperty.Parse(vcard))
        {
            switch (property.Name)
            {
                case "UID": uid = property.Value; break;
                case "FN": formattedName = Unescape(property.Value); break;
                case "N":
                    var parts = SplitComponents(property.Value);
                    family = NullIfEmpty(parts.ElementAtOrDefault(0));
                    given = NullIfEmpty(parts.ElementAtOrDefault(1));
                    break;
                case "ORG": organization = NullIfEmpty(SplitComponents(property.Value)[0]); break;
                case "TITLE": title = NullIfEmpty(Unescape(property.Value)); break;
                case "NOTE": note = NullIfEmpty(Unescape(property.Value)); break;
                case "EMAIL": emails.Add(new ContactField(Unescape(property.Value).Trim(), property.Kind)); break;
                case "TEL":
                    var number = Unescape(property.Value).Trim();
                    phones.Add(new ContactField(number.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? number[4..] : number, property.Kind));
                    break;
                case "KIND" or "X-ADDRESSBOOKSERVER-KIND":
                    kind = property.Value.Trim().Equals("group", StringComparison.OrdinalIgnoreCase) ? ContactKind.Group : kind;
                    break;
                case "MEMBER" or "X-ADDRESSBOOKSERVER-MEMBER":
                    if (ReadMember(property) is { } member)
                    {
                        members.Add(member);
                    }

                    break;
                case "PHOTO":
                    photo ??= ReadPhoto(property);
                    break;
            }
        }

        var structuredName = $"{given} {family}".Trim();
        var displayName = FirstNonEmpty(formattedName, structuredName, organization, emails.FirstOrDefault()?.Value) ?? T("(ohne Namen)");
        return new ContactCard(uid ?? string.Empty, displayName, given, family, organization, title, note, emails, phones)
        {
            Kind = kind,
            Members = members,
            Photo = photo,
        };
    }

    // urn:uuid:<uid> (optionally with our X-NERUNA-EMAIL parameter) or mailto:<address>.
    private static GroupMember? ReadMember(VCardProperty property)
    {
        var value = property.Value.Trim();
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return new GroupMember(null, Uri.UnescapeDataString(value[7..]));
        }

        var uid = ContactCard.NormalizeUid(value);
        return uid.Length == 0 ? null : new GroupMember(uid, property.Parameter(GroupDraft.EmailParameter));
    }

    // vCard 4: data: URI; vCard 3/2.1: ENCODING=b/BASE64 with TYPE=JPEG. Linked (http) photos are not loaded.
    private static ContactPhoto? ReadPhoto(VCardProperty property)
    {
        try
        {
            var value = property.Value.Trim();
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = value.IndexOf(',', StringComparison.Ordinal);
                var header = value[5..Math.Max(5, comma)];
                var mediaType = header.Split(';')[0];
                return comma < 0 || !header.Contains("base64", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new ContactPhoto(Convert.FromBase64String(value[(comma + 1)..]), mediaType.Length > 0 ? mediaType : "image/jpeg");
            }

            var encoding = property.Parameter("ENCODING");
            var bare = property.Parameters.Split(';').Select(p => p.Trim().ToUpperInvariant());
            if (encoding is not null && (encoding.Equals("b", StringComparison.OrdinalIgnoreCase) || encoding.Equals("BASE64", StringComparison.OrdinalIgnoreCase))
                || bare.Contains("BASE64"))
            {
                var type = (property.Parameter("TYPE") ?? bare.FirstOrDefault(p => p is "JPEG" or "PNG" or "GIF") ?? "JPEG").ToLowerInvariant();
                var mediaType = type.Contains('/', StringComparison.Ordinal) ? type : "image/" + (type == "jpg" ? "jpeg" : type);
                return new ContactPhoto(Convert.FromBase64String(value), mediaType);
            }
        }
        catch (FormatException)
        {
            // A broken picture is no reason to fail reading the contact.
        }

        return null;
    }

    internal static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                result.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>Splits a structured value (N, ORG) at unescaped semicolons and unescapes each part.</summary>
    internal static List<string> SplitComponents(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value[i]).Append(value[++i]);
            }
            else if (value[i] == ';')
            {
                parts.Add(Unescape(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }

        parts.Add(Unescape(current.ToString()));
        return parts;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>One unfolded content line: <c>[group.]NAME[;params]:value</c>.</summary>
internal sealed record VCardProperty(string RawLine, string Name, string Parameters, string Value)
{
    /// <summary>The first TYPE that is not just "internet"/"voice"/"pref", lower-case.</summary>
    public string? Kind
    {
        get
        {
            foreach (var parameter in Parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = parameter.Trim();
                var values = p.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase) ? p[5..]
                    : p.Contains('=', StringComparison.Ordinal) ? null
                    : p; // vCard 2.1 style bare parameter
                if (values is null)
                {
                    continue;
                }

                foreach (var value in values.Trim('"').Split(','))
                {
                    var kind = value.Trim().ToLowerInvariant();
                    if (kind is not ("internet" or "voice" or "pref" or "x400" or ""))
                    {
                        return kind;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>The value of a <c>NAME=value</c> parameter (quotes removed), or null.</summary>
    public string? Parameter(string name)
    {
        foreach (var parameter in Parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = parameter.Trim();
            if (p.Length > name.Length && p[name.Length] == '=' && p.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return p[(name.Length + 1)..].Trim('"');
            }
        }

        return null;
    }

    public static IEnumerable<VCardProperty> Parse(string vcard)
    {
        foreach (var line in Unfold(vcard))
        {
            var colon = IndexOfUnquoted(line, ':');
            if (colon <= 0)
            {
                continue;
            }

            var head = line[..colon];
            var semicolon = head.IndexOf(';', StringComparison.Ordinal);
            var name = semicolon >= 0 ? head[..semicolon] : head;
            var dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                name = name[(dot + 1)..];
            }

            yield return new VCardProperty(line, name.ToUpperInvariant(), semicolon >= 0 ? head[(semicolon + 1)..] : string.Empty, line[(colon + 1)..]);
        }
    }

    public static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear().Append(line);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static int IndexOfUnquoted(string line, char c)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == c && !quoted)
            {
                return i;
            }
        }

        return -1;
    }
}
