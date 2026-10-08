using Neruna.Core.Mail;

namespace Neruna.Core.Contacts;

/// <summary>
/// Something to send to, from the address books: one address of a contact, or a group with its members' addresses.
/// </summary>
/// <param name="Name">Contact or group name.</param>
/// <param name="Addresses">One address for a contact, the members' addresses for a group.</param>
/// <param name="Detail">Organization of a contact, or e.g. "Gruppe · 4 Mitglieder".</param>
public sealed record RecipientEntry(string Name, IReadOnlyList<MailAddress> Addresses, string? Detail, bool IsGroup)
{
    /// <summary>The address shown below the name (a group shows its member count in <see cref="Detail"/>).</summary>
    public string? Address => IsGroup ? null : Addresses[0].Address;

    /// <summary>
    /// How well <paramref name="query"/> matches: 0 = not at all, higher is better (name or a word of it starts with it,
    /// then the address starts with it, then it occurs anywhere).
    /// </summary>
    public int Score(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.Trim();
        if (query.Length == 0)
        {
            return 1;
        }

        if (Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 4;
        }

        if (Name.Split([' ', '-', ',', '.'], StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            || (!IsGroup && Addresses[0].Address.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 3;
        }

        return Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (!IsGroup && Addresses[0].Address.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (Detail?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
            ? 2
            : 0;
    }

    /// <summary>The addresses as typed into An/Cc: <c>Anna Muster &lt;anna@example.com&gt;</c>, quoted where needed.</summary>
    public IEnumerable<string> FieldTexts => Addresses.Select(Format);

    private static readonly System.Buffers.SearchValues<char> Specials = System.Buffers.SearchValues.Create(",;<>\"@:()[]\\.");

    public static string Format(MailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (string.IsNullOrWhiteSpace(address.Name))
        {
            return address.Address;
        }

        // Quotes only where the address syntax needs them ("Muster, Anna", "Dr. Muster"), so the field stays readable.
        var name = address.Name.Trim();
        return name.AsSpan().IndexOfAny(Specials) >= 0
            ? $"\"{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\" <{address.Address}>"
            : $"{name} <{address.Address}>";
    }

    /// <summary>Best matches first; ties alphabetically.</summary>
    /// <param name="startsOnly">Only names, words or addresses starting with the query (while typing an address:
    /// "le" should find Lea, not every "example.com").</param>
    public static IReadOnlyList<RecipientEntry> Find(IEnumerable<RecipientEntry> all, string query, int max = int.MaxValue, bool startsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(all);
        return all.Select(e => (Entry: e, Score: e.Score(query)))
            .Where(x => x.Score > (startsOnly ? 2 : 0))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.Entry)
            .ToList();
    }
}
