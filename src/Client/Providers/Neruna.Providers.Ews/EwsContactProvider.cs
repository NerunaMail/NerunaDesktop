using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Contacts;

namespace Neruna.Providers.Ews;

/// <summary>
/// Contacts through Exchange Web Services: the contact folders of the mailbox, as vCard 3.0 (and back). The version of
/// a contact is its change key. Read: names, company, addresses, up to three e-mail addresses, phone numbers, notes,
/// birthday. Written: what <see cref="ContactDraft"/> edits; addresses and other fields on the server stay as they are.
/// Distribution lists and pictures are not synced yet.
/// </summary>
internal sealed class EwsContactProvider(ServiceConnection connection, EwsConnectionFactory connections) : IContactProvider
{
    private static readonly object[] Fields =
    [
        "item:Body", "contacts:DisplayName", "contacts:GivenName", "contacts:CompanyName", "contacts:EmailAddresses",
        "contacts:PhysicalAddresses", "contacts:PhoneNumbers", "contacts:Birthday", "contacts:JobTitle", "contacts:Surname",
    ];

    // Where phone numbers go, by kind: the slots Exchange has (in order of use).
    private static readonly string[] MobileSlots = ["MobilePhone"];
    private static readonly string[] HomeSlots = ["HomePhone", "HomePhone2"];
    private static readonly string[] WorkSlots = ["BusinessPhone", "BusinessPhone2", "OtherTelephone"];
    private static readonly string[] FaxSlots = ["BusinessFax"];
    private static readonly string[] EmailSlots = ["EmailAddress1", "EmailAddress2", "EmailAddress3"];

    private EwsClient? _client;

    public ContactProviderCapabilities Capabilities => ContactProviderCapabilities.Write;

    private async Task<EwsClient> ClientAsync(CancellationToken cancellationToken) =>
        _client ??= await connections.ClientForAsync(connection, cancellationToken);

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await EwsOperations.WellKnownAsync(await ClientAsync(cancellationToken), ["contacts"], cancellationToken);

    public async Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(cancellationToken);
        var main = (await EwsOperations.WellKnownAsync(client, ["contacts"], cancellationToken)).GetValueOrDefault("contacts");
        var (_, folders) = await EwsOperations.FindFoldersAsync(client, cancellationToken, "folder:EffectiveRights");

        // Only real contact folders: Exchange keeps suggested and organisation contacts in sub-classes of its own.
        return [.. folders.Where(f => f.Val("FolderClass") == "IPF.Contact")
            .OrderBy(f => EwsOperations.FolderIdOf(f) != main)
            .Select(f => new AddressBookInfo(connection.Id, EwsOperations.FolderIdOf(f), f.Val("DisplayName") ?? string.Empty,
                f.Get("EffectiveRights") is { } rights && !rights.Bool("CreateContents")))];
    }

    public async Task<AddressBookSyncResult> SyncAddressBookAsync(AddressBookInfo addressBook, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(knownVersions);
        var client = await ClientAsync(cancellationToken);
        var (listed, _) = await EwsOperations.FindItemIdsAsync(client, addressBook.RemoteId, cancellationToken, kind: "Contact");
        var changed = listed.Where(i => !knownVersions.TryGetValue(i.Id, out var version) || version != i.ChangeKey).Select(i => i.Id);
        var contacts = await EwsOperations.GetItemsAsync(client, changed, EwsOperations.Shape(Fields, "Text"), cancellationToken);
        var present = listed.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return new AddressBookSyncResult(string.Empty, false, [.. contacts.Select(ToObject)], [.. knownVersions.Keys.Where(k => !present.Contains(k))]);
    }

    public async Task<ContactObject> SaveAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(contact);
        var client = await ClientAsync(cancellationToken);
        var fields = FieldsOf(ContactDraft.FromVCard(contact.VCardData));
        string id;
        if (string.IsNullOrEmpty(contact.RemoteId))
        {
            var created = (await client.CallAsync(X.Msg("CreateItem",
                X.Msg("SavedItemFolderId", X.FolderId(addressBook.RemoteId)),
                X.Msg("Items", X.Typ("Contact", CreateElements(fields)))), cancellationToken)).Single();
            id = created.FirstItem().Id() ?? throw new EwsException(null, "Exchange returned no item id.");
        }
        else
        {
            var current = (await EwsOperations.GetItemsAsync(client, [contact.RemoteId], EwsOperations.Shape(Fields, "Text"), cancellationToken)).FirstOrDefault()
                          ?? throw new RemoteConflictException();
            if (contact.ETag is not null && current.ChangeKey() != contact.ETag)
            {
                throw new RemoteConflictException();
            }

            // Only what changed – fields Neruna does not edit stay untouched.
            var before = FieldsOf(ContactDraft.FromVCard(ToVCard(current))).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            var updates = fields.Where(f => before.GetValueOrDefault(f.Key) != f.Value).Select(Update).ToList();
            if (updates.Count > 0)
            {
                await client.CallAsync(X.Msg("UpdateItem", new XAttribute("ConflictResolution", "NeverOverwrite"),
                    X.Msg("ItemChanges", X.Typ("ItemChange", X.ItemId(current.Id()!, current.ChangeKey()), X.Typ("Updates", updates)))), cancellationToken);
            }

            id = contact.RemoteId;
        }

        var saved = (await EwsOperations.GetItemsAsync(client, [id], EwsOperations.Shape(Fields, "Text"), cancellationToken)).FirstOrDefault() ?? throw new RemoteConflictException();
        return ToObject(saved);
    }

    public async Task DeleteAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var client = await ClientAsync(cancellationToken);
        await client.CallAsync(X.Msg("DeleteItem", new XAttribute("DeleteType", "MoveToDeletedItems"),
            X.Msg("ItemIds", X.ItemId(contact.RemoteId))), cancellationToken, "ErrorItemNotFound");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ContactObject ToObject(XElement c) => new(c.Id()!, c.ChangeKey(), ToVCard(c));

    /// <summary>An Exchange contact as vCard 3.0; the UID is the item id.</summary>
    internal static string ToVCard(XElement c)
    {
        string? Entry(string list, string key) =>
            c.Get(list)?.Elements(X.T + "Entry").FirstOrDefault(e => e.Attribute("Key")?.Value == key)?.Value is { Length: > 0 } value ? value.Trim() : null;
        IEnumerable<ContactField> Phones(string[] slots, string kind) => slots.Select(s => Entry("PhoneNumbers", s)).OfType<string>().Select(v => new ContactField(v, kind));

        var draft = new ContactDraft(
            c.Val("GivenName"), c.Val("Surname"), c.Val("CompanyName"), c.Val("JobTitle"),
            [.. EmailSlots.Select(s => Entry("EmailAddresses", s)).OfType<string>().Where(e => e.Contains('@', StringComparison.Ordinal)).Select(e => new ContactField(e))],
            [.. Phones(WorkSlots, "work"), .. Phones(HomeSlots, "home"), .. Phones(MobileSlots, "cell"), .. Phones(FaxSlots, "fax")],
            c.Val("Body") is { Length: > 0 } note ? note.Trim() : null)
        {
            Birthday = c.Date("Birthday") is { } birthday ? BirthdayOf(birthday) : null,
        };

        var card = new StringBuilder("BEGIN:VCARD\r\nVERSION:3.0\r\n").Append("UID:").Append(c.Id()).Append("\r\n");
        foreach (var address in c.Get("PhysicalAddresses")?.Elements(X.T + "Entry") ?? [])
        {
            var kind = address.Attribute("Key")?.Value switch { "Business" => "WORK", "Home" => "HOME", _ => "OTHER" };
            string[] parts = [string.Empty, string.Empty, address.Val("Street") ?? string.Empty, address.Val("City") ?? string.Empty,
                address.Val("State") ?? string.Empty, address.Val("PostalCode") ?? string.Empty, address.Val("CountryOrRegion") ?? string.Empty];
            if (parts.Any(p => p.Length > 0))
            {
                card.Append("ADR;TYPE=").Append(kind).Append(':').Append(string.Join(';', parts.Select(Escape))).Append("\r\n");
            }
        }

        return draft.ToVCard(card.Append("END:VCARD\r\n").ToString());
    }

    // Exchange keeps a birthday as a point in time – often midnight in the zone of whoever entered it: the nearest day.
    private static ContactBirthday BirthdayOf(DateTimeOffset value)
    {
        var day = value.UtcDateTime.AddHours(12);
        return new ContactBirthday(day.Year, day.Month, day.Day);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// What Neruna writes, keyed like EWS fields ("contacts:GivenName", "contacts:PhoneNumber/MobilePhone" …), in schema
    /// order; null values remove the field.
    /// </summary>
    internal static List<(string Key, string? Value)> FieldsOf(ContactDraft draft)
    {
        static bool Is(ContactField phone, string kind) =>
            phone.Kind?.Split(',').Any(k => string.Equals(k.Trim(), kind, StringComparison.OrdinalIgnoreCase)) ?? false;
        var phones = draft.Phones.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToList();
        var mobile = phones.Where(p => Is(p, "cell")).ToList();
        var fax = phones.Where(p => !mobile.Contains(p) && Is(p, "fax")).ToList();
        var home = phones.Where(p => !mobile.Contains(p) && !fax.Contains(p) && Is(p, "home")).ToList();
        var work = phones.Where(p => !mobile.Contains(p) && !fax.Contains(p) && !home.Contains(p)).ToList();
        static IEnumerable<(string, string?)> Slots(string field, string[] slots, IEnumerable<string> values)
        {
            var list = values.ToList();
            return slots.Select((slot, i) => ($"{field}/{slot}", i < list.Count ? list[i] : null));
        }

        string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return
        [
            ("item:Body", Text(draft.Note)),
            ("contacts:DisplayName", Text(draft.DisplayName)),
            ("contacts:GivenName", Text(draft.GivenName)),
            ("contacts:CompanyName", Text(draft.Organization)),
            // Exchange keeps three addresses per contact.
            .. Slots("contacts:EmailAddress", EmailSlots, draft.Emails.Select(e => e.Value.Trim()).Where(e => e.Length > 0)),
            .. Slots("contacts:PhoneNumber", [.. MobileSlots, .. HomeSlots, .. WorkSlots, .. FaxSlots],
                [.. Pad(mobile, MobileSlots.Length), .. Pad(home, HomeSlots.Length), .. Pad(work, WorkSlots.Length), .. Pad(fax, FaxSlots.Length)]),
            // Exchange needs a year; without one the birthday stays only here.
            ("contacts:Birthday", draft.Birthday is { Year: { } year } b ? $"{year:0000}-{b.Month:00}-{b.Day:00}T12:00:00Z" : null),
            ("contacts:JobTitle", Text(draft.Title)),
            ("contacts:Surname", Text(draft.FamilyName)),
        ];
    }

    // The values for a group of slots, with gaps for those left empty (a list of exactly that many).
    private static IEnumerable<string> Pad(List<ContactField> phones, int slots) =>
        Enumerable.Range(0, slots).Select(i => i < phones.Count ? phones[i].Value.Trim() : string.Empty);

    private static IEnumerable<XElement> CreateElements(List<(string Key, string? Value)> fields)
    {
        foreach (var group in fields.Where(f => !string.IsNullOrEmpty(f.Value)).GroupBy(f => f.Key.Split('/')[0]))
        {
            var key = group.Key;
            if (key is "contacts:EmailAddress" or "contacts:PhoneNumber")
            {
                yield return X.Typ(key == "contacts:EmailAddress" ? "EmailAddresses" : "PhoneNumbers",
                    group.Select(f => X.Typ("Entry", new XAttribute("Key", f.Key.Split('/')[1]), f.Value)));
            }
            else
            {
                yield return Element(key, group.First().Value!);
            }
        }
    }

    private static XElement Update((string Key, string? Value) field)
    {
        var parts = field.Key.Split('/');
        if (parts.Length == 2)
        {
            var uri = X.Typ("IndexedFieldURI", new XAttribute("FieldURI", parts[0]), new XAttribute("FieldIndex", parts[1]));
            return string.IsNullOrEmpty(field.Value)
                ? X.Typ("DeleteItemField", uri)
                : X.Typ("SetItemField", uri, X.Typ("Contact", X.Typ(parts[0] == "contacts:EmailAddress" ? "EmailAddresses" : "PhoneNumbers",
                    X.Typ("Entry", new XAttribute("Key", parts[1]), field.Value))));
        }

        return string.IsNullOrEmpty(field.Value)
            ? X.Typ("DeleteItemField", X.Field(field.Key))
            : X.Typ("SetItemField", X.Field(field.Key), X.Typ("Contact", Element(field.Key, field.Value)));
    }

    private static XElement Element(string field, string value) =>
        field == "item:Body" ? X.Typ("Body", new XAttribute("BodyType", "Text"), value) : X.Typ(field[(field.IndexOf(':', StringComparison.Ordinal) + 1)..], value);
}
