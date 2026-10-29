namespace Neruna.Core.Contacts;

/// <summary>
/// Duplicates among the contacts – the same person twice, e.g. once with the phone number and once with the address –
/// and merging them into one card that keeps everything either had.
/// </summary>
public static class ContactMerge
{
    // What ContactDraft writes itself (merged field by field) or what belongs to the card that stays.
    private static readonly HashSet<string> NotCopied =
        ["BEGIN", "END", "VERSION", "UID", "PRODID", "REV", "FN", "N", "ORG", "TITLE", "EMAIL", "TEL", "NOTE", "BDAY", "PHOTO"];

    /// <summary>
    /// Contacts that are probably the same person: the same name (in any word order), a shared address or a shared
    /// phone number. Groups, read-only address books and "Verwendete Adressen" are left out (the latter clears itself).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ContactEntry>> FindDuplicates(IEnumerable<ContactEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var candidates = entries
            .Where(e => !e.Card.IsGroup && !e.AddressBook.IsReadOnly && e.AddressBook.RemoteId != AddressCollector.AddressBookId)
            .ToList();

        // Union-find over the candidates: every shared key joins two of them.
        var parent = Enumerable.Range(0, candidates.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        var firstWithKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var key in Keys(candidates[i].Card))
            {
                if (firstWithKey.TryGetValue(key, out var other))
                {
                    parent[Root(i)] = Root(other);
                }
                else
                {
                    firstWithKey[key] = i;
                }
            }
        }

        return candidates
            .Select((entry, index) => (entry, root: Root(index)))
            .GroupBy(x => x.root)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<ContactEntry>)[.. g.Select(x => x.entry)])
            .OrderBy(g => g[0].Card.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> Keys(ContactCard card)
    {
        if (NameKey(card.DisplayName) is { } name)
        {
            yield return "n:" + name;
        }

        foreach (var email in card.EmailAddresses.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            yield return "e:" + email.Trim().ToLowerInvariant();
        }

        foreach (var phone in card.PhoneNumbers.Select(PhoneKey).OfType<string>())
        {
            yield return "t:" + phone;
        }
    }

    // "Anna Muster" and "muster  anna" are the same name; an address shown as the name is no name.
    private static string? NameKey(string displayName)
    {
        var words = displayName.Split((char[])[' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .Order(StringComparer.Ordinal)
            .ToList();
        return words.Count == 0 || displayName.Contains('@', StringComparison.Ordinal) ? null : string.Join(' ', words);
    }

    /// <summary>
    /// The number without spaces or prefix: "+41 79 123 45 67", "0041791234567" and "079 123 45 67" are the same (the
    /// last nine digits); short numbers are compared as they are.
    /// </summary>
    internal static string? PhoneKey(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length switch
        {
            < 5 => null,
            > 9 => digits[^9..],
            _ => digits,
        };
    }

    /// <summary>
    /// One card from two: <paramref name="keep"/> with what <paramref name="other"/> adds – empty fields filled,
    /// addresses and numbers joined, both notes, the picture if it had none, and everything else of the other card
    /// (postal addresses, websites, certificates …) that it does not have yet.
    /// </summary>
    public static string Merge(string keep, string other)
    {
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(other);
        var k = ContactDraft.FromVCard(keep);
        var o = ContactDraft.FromVCard(other);

        var merged = k with
        {
            GivenName = Fill(k.GivenName, o.GivenName),
            FamilyName = Fill(k.FamilyName, o.FamilyName),
            Organization = Fill(k.Organization, o.Organization),
            Title = Fill(k.Title, o.Title),
            Emails = Join(k.Emails, o.Emails, e => e.Trim().ToLowerInvariant()),
            Phones = Join(k.Phones, o.Phones, p => PhoneKey(p) ?? p.Trim()),
            Note = JoinNotes(k.Note, o.Note),
            Birthday = k.Birthday ?? o.Birthday,
        };
        if (k.Photo is null && o.Photo is not null)
        {
            merged = merged with { Photo = o.Photo, ReplacePhoto = true };
        }

        var card = merged.ToVCard(keep);
        var present = VCardProperty.Parse(card).Select(Same).ToHashSet(StringComparer.Ordinal);
        var added = VCardProperty.Parse(other)
            .Where(p => !NotCopied.Contains(p.Name) && !p.Name.StartsWith("X-ABLABEL", StringComparison.Ordinal) && present.Add(Same(p)))
            .Select(p => WithoutGroup(p.RawLine))
            .ToList();
        if (added.Count == 0)
        {
            return card;
        }

        var end = card.LastIndexOf("END:VCARD", StringComparison.OrdinalIgnoreCase);
        return card[..end] + string.Join("\r\n", added) + "\r\n" + card[end..];
    }

    private static string? Fill(string? value, string? other) => string.IsNullOrWhiteSpace(value) ? other : value;

    private static List<ContactField> Join(IReadOnlyList<ContactField> first, IReadOnlyList<ContactField> second, Func<string, string> key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. first.Concat(second).Where(f => !string.IsNullOrWhiteSpace(f.Value) && seen.Add(key(f.Value)))];
    }

    private static string? JoinNotes(string? first, string? second) =>
        string.IsNullOrWhiteSpace(second) || first?.Contains(second.Trim(), StringComparison.Ordinal) == true ? first
        : string.IsNullOrWhiteSpace(first) ? second
        : first.TrimEnd() + "\n\n" + second.Trim();

    // The same property with the same value is not copied twice (a group prefix like "item1." does not count).
    private static string Same(VCardProperty property) => property.Name + ":" + property.Value.Trim();

    // Apple's "item1.ADR" with its "item1.X-ABLabel": the label is not copied, so the prefix goes too – it could
    // clash with the kept card's own "item1".
    private static string WithoutGroup(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        var dot = line.IndexOf('.', StringComparison.Ordinal);
        var semicolon = line.IndexOf(';', StringComparison.Ordinal);
        var headEnd = semicolon >= 0 && semicolon < colon ? semicolon : colon;
        return dot > 0 && dot < headEnd ? line[(dot + 1)..] : line;
    }
}
