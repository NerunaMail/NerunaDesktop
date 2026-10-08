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
