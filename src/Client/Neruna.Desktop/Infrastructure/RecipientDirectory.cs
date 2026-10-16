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

    /// <summary>The contact with this address (the first one found), for the placeholders of text templates.</summary>
    public async Task<ContactCard?> FindContactAsync(string email)
    {
        if (_cards is null || _cards.IsFaulted || _cards.IsCanceled || DateTime.UtcNow - _cardsAt > KeepFor)
        {
            _cardsAt = DateTime.UtcNow;
            _cards = Task.Run(async () =>
            {
                var byAddress = new Dictionary<string, ContactCard>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in await contacts.SearchAsync(null))
                {
                    foreach (var address in entry.Card.IsGroup ? [] : entry.Card.EmailAddresses)
                    {
                        byAddress.TryAdd(address, entry.Card);
                    }
                }

                return byAddress;
            });
        }

        return (await _cards).GetValueOrDefault(email);
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
