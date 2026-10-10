using System.Text.Json;
using Neruna.Core;
using Neruna.Core.Contacts;

namespace Neruna.Providers.Graph;

/// <summary>
/// Contacts through Microsoft Graph: the default contacts folder ("Kontakte") and the user's other contact folders,
/// converted to vCard (and back). The version of a contact is Graph's change key. Pictures are not synced yet.
/// </summary>
internal sealed class GraphContactProvider(Guid connectionId, GraphClient graph) : IContactProvider
{
    /// <summary>Remote id of the default folder (Graph addresses it without an id).</summary>
    public const string DefaultFolder = "default";

    private const string ContactFields = "id,changeKey,displayName,givenName,surname,companyName,jobTitle,emailAddresses,businessPhones,homePhones,mobilePhone,personalNotes";

    public ContactProviderCapabilities Capabilities => ContactProviderCapabilities.Write;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await graph.GetAsync("me/contacts?$top=1&$select=id", cancellationToken);

    public async Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default) =>
        [new AddressBookInfo(connectionId, DefaultFolder, Neruna.Core.Localization.Texts.T("Kontakte"), false),
         .. (await graph.GetAllAsync("me/contactFolders?$top=100&$select=id,displayName", cancellationToken))
             .Select(f => new AddressBookInfo(connectionId, f.Str("id")!, f.Str("displayName") ?? string.Empty, false))];

    public async Task<AddressBookSyncResult> SyncAddressBookAsync(AddressBookInfo addressBook, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(knownVersions);
        var contacts = await graph.GetAllAsync($"{FolderPath(addressBook)}?$select={ContactFields}&$top=250", cancellationToken);
        var changed = contacts
            .Where(c => !knownVersions.TryGetValue(c.Str("id")!, out var version) || version != c.Str("changeKey"))
            .Select(ToObject)
            .ToList();
        var present = contacts.Select(c => c.Str("id")).ToHashSet(StringComparer.Ordinal);
        return new AddressBookSyncResult(string.Empty, false, changed, [.. knownVersions.Keys.Where(k => !present.Contains(k))]);
    }

    public async Task<ContactObject> SaveAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(contact);
        var body = ToGraph(contact.VCardData);
        JsonElement saved;
        if (string.IsNullOrEmpty(contact.RemoteId))
        {
            saved = await graph.SendJsonAsync(HttpMethod.Post, FolderPath(addressBook), body, cancellationToken);
        }
        else
        {
            var current = await graph.GetAsync($"me/contacts/{contact.RemoteId}?$select=changeKey", cancellationToken);
            if (contact.ETag is not null && current.Str("changeKey") != contact.ETag)
            {
                throw new RemoteConflictException();
            }

            saved = await graph.SendJsonAsync(HttpMethod.Patch, $"me/contacts/{contact.RemoteId}", body, cancellationToken);
        }

        return ToObject(saved);
    }

    public async Task DeleteAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        await graph.SendJsonAsync(HttpMethod.Delete, $"me/contacts/{contact.RemoteId}", null, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string FolderPath(AddressBookInfo addressBook) =>
        addressBook.RemoteId == DefaultFolder ? "me/contacts" : $"me/contactFolders/{addressBook.RemoteId}/contacts";

    private static ContactObject ToObject(JsonElement c) => new(c.Str("id")!, c.Str("changeKey"), ToVCard(c));

    /// <summary>A Graph contact as vCard 3.0; the UID is Graph's id, so it stays the same across syncs.</summary>
    internal static string ToVCard(JsonElement c)
    {
        var phones = c.Arr("businessPhones").Select(p => new ContactField(p.GetString()!, "work"))
            .Concat(c.Arr("homePhones").Select(p => new ContactField(p.GetString()!, "home")))
            .Concat(c.Str("mobilePhone") is { Length: > 0 } mobile ? [new ContactField(mobile, "cell")] : [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToList();
        var draft = new ContactDraft(
            c.Str("givenName"), c.Str("surname"), c.Str("companyName"), c.Str("jobTitle"),
            [.. c.Arr("emailAddresses").Where(e => e.Str("address") is not null).Select(e => new ContactField(e.Str("address")!))],
            phones,
            c.Str("personalNotes") is { Length: > 0 } note ? note : null)
        {
            // Graph: "1985-04-15T11:59:00Z" (the time is meaningless).
            Birthday = ContactBirthday.Parse(c.Str("birthday")),
        };
        return draft.ToVCard($"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{c.Str("id")}\r\nEND:VCARD\r\n");
    }

    /// <summary>A vCard (as Neruna edits it) as a Graph contact.</summary>
    internal static Dictionary<string, object?> ToGraph(string vcard)
    {
        var draft = ContactDraft.FromVCard(vcard);
        bool Is(ContactField phone, string kind) => string.Equals(phone.Kind, kind, StringComparison.OrdinalIgnoreCase)
            || (phone.Kind?.Split(',').Any(k => string.Equals(k.Trim(), kind, StringComparison.OrdinalIgnoreCase)) ?? false);
        var mobile = draft.Phones.FirstOrDefault(p => Is(p, "cell"));
        return new Dictionary<string, object?>
        {
            ["givenName"] = draft.GivenName,
            ["surname"] = draft.FamilyName,
            ["displayName"] = draft.DisplayName,
            ["companyName"] = draft.Organization,
            ["jobTitle"] = draft.Title,
            ["personalNotes"] = draft.Note,
            // Graph needs a year; without one the birthday stays only here.
            ["birthday"] = draft.Birthday is { Year: { } year } b ? $"{year:0000}-{b.Month:00}-{b.Day:00}T11:59:00Z" : null,
            // Graph keeps at most three addresses per contact.
            ["emailAddresses"] = draft.Emails.Take(3).Select(e => new { address = e.Value, name = draft.DisplayName }).ToList(),
            ["mobilePhone"] = mobile?.Value,
            ["homePhones"] = draft.Phones.Where(p => p != mobile && Is(p, "home")).Select(p => p.Value).ToList(),
            ["businessPhones"] = draft.Phones.Where(p => p != mobile && !Is(p, "home")).Select(p => p.Value).ToList(),
        };
    }
}
