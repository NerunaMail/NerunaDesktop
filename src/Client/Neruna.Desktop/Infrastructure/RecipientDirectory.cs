using Neruna.Core.Contacts;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// The address books as recipients (contacts and groups), for An/Cc completion and the contact picker. Read once in the
/// background and kept briefly, so typing does not parse every vCard again. Used from the UI thread only.
/// </summary>
internal sealed class RecipientDirectory(ContactController contacts)
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(2);
    private Task<IReadOnlyList<RecipientEntry>>? _load;
    private DateTime _loadedAt;
    private Task<Dictionary<string, ContactCard>>? _cards;
    private DateTime _cardsAt;

    /// <summary>The contact with this address (the first one found, one with a picture first), for the placeholders of text templates and the sender's picture.</summary>
    public async Task<ContactCard?> FindContactAsync(string email)
    {
        if (_cards is null || _cards.IsFaulted || _cards.IsCanceled || DateTime.UtcNow - _cardsAt > KeepFor)
        {
            _cardsAt = DateTime.UtcNow;
            _cards = Task.Run(async () =>
            {
                var byAddress = new Dictionary<string, ContactCard>(StringComparer.OrdinalIgnoreCase);
                // "Verwendete Adressen" are no contacts of their own (see AddressCollector).
                foreach (var entry in (await contacts.SearchAsync(null)).Where(e => e.AddressBook.RemoteId != AddressCollector.AddressBookId))
                {
                    foreach (var address in entry.Card.IsGroup ? [] : entry.Card.EmailAddresses)
                    {
                        // The same person in several address books: the one with a picture (shown for the sender).
                        if (!byAddress.TryAdd(address, entry.Card) && byAddress[address].Photo is null && entry.Card.Photo is not null)
                        {
                            byAddress[address] = entry.Card;
                        }
                    }
                }

                return byAddress;
            });
        }

        return (await _cards).GetValueOrDefault(email);
    }

    /// <summary>The address books changed (e.g. addresses kept after sending): read them again next time.</summary>
    public void Forget()
    {
        _load = null;
        _cards = null;
    }

    public Task<IReadOnlyList<RecipientEntry>> GetAllAsync()
    {
        if (_load is null || _load.IsFaulted || _load.IsCanceled || DateTime.UtcNow - _loadedAt > KeepFor)
        {
            _loadedAt = DateTime.UtcNow;
            _load = Task.Run(() => contacts.GetRecipientsAsync());
        }

        return _load;
    }

    /// <summary>Suggestions while typing: names/addresses starting with <paramref name="query"/>, without addresses
    /// already in the field.</summary>
    public async Task<IReadOnlyList<RecipientEntry>> SuggestAsync(string query, string field, int max) =>
        RecipientEntry.Find(
            (await GetAllAsync()).Where(e => e.IsGroup || !field.Contains(e.Addresses[0].Address, StringComparison.OrdinalIgnoreCase)),
            query,
            max,
            startsOnly: true);
}
