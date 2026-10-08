using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;

namespace Neruna.Core;

public interface IAccountStore
{
    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default);

    Task SaveAccountAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>Stores the order the user arranged the accounts in (first = top).</summary>
    Task SetOrderAsync(IReadOnlyList<Guid> accountIds, CancellationToken cancellationToken = default);

    /// <summary>Deletes the account and all cached data of its connections.</summary>
    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>Offline cache for mail. Implementations must apply each sync result atomically.</summary>
public interface IMailStore
{
    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the stored folder list match <paramref name="remoteFolders"/>: adds new folders, removes vanished ones
    /// (with their messages) and keeps the sync state of existing ones. Returns the merged list.
    /// </summary>
    Task<IReadOnlyList<MailFolder>> MergeFoldersAsync(Guid connectionId, IReadOnlyList<MailFolder> remoteFolders, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<string>> GetMessageIdsAsync(Guid connectionId, string folderRemoteId, CancellationToken cancellationToken = default);

    Task ApplySyncResultAsync(MailFolder folder, FolderSyncResult result, CancellationToken cancellationToken = default);

    /// <summary>Removes messages locally right after they were moved or deleted on the server.</summary>
    Task RemoveMessagesAsync(Guid connectionId, string folderRemoteId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid connectionId, string folderRemoteId, int skip, int take, CancellationToken cancellationToken = default);

    Task UpdateFlagsAsync(Guid connectionId, string folderRemoteId, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default);

    /// <summary>Raw MIME of a downloaded message, or null if not cached yet.</summary>
    Task<Stream?> OpenMessageContentAsync(Guid connectionId, string folderRemoteId, string remoteId, CancellationToken cancellationToken = default);

    Task SaveMessageContentAsync(Guid connectionId, string folderRemoteId, string remoteId, Stream mime, CancellationToken cancellationToken = default);
}

public interface ICalendarStore
{
    Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(Guid? connectionId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarInfo>> MergeCalendarsAsync(Guid connectionId, IReadOnlyList<CalendarInfo> remoteCalendars, CancellationToken cancellationToken = default);

    /// <summary>Remote id → ETag of every stored object.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetObjectVersionsAsync(Guid connectionId, string calendarRemoteId, CancellationToken cancellationToken = default);

    Task ApplySyncResultAsync(CalendarInfo calendar, CalendarSyncResult result, CancellationToken cancellationToken = default);

    /// <summary>Stores one object after a successful write to the server (keeps the calendar's sync state).</summary>
    Task UpsertObjectAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default);

    Task DeleteObjectAsync(CalendarInfo calendar, string remoteId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarObject>> GetObjectsAsync(Guid connectionId, string calendarRemoteId, CancellationToken cancellationToken = default);
}

public interface IContactStore
{
    Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(Guid? connectionId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AddressBookInfo>> MergeAddressBooksAsync(Guid connectionId, IReadOnlyList<AddressBookInfo> remoteAddressBooks, CancellationToken cancellationToken = default);

    /// <summary>Remote id → ETag of every stored contact.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetContactVersionsAsync(Guid connectionId, string addressBookRemoteId, CancellationToken cancellationToken = default);

    Task ApplySyncResultAsync(AddressBookInfo addressBook, AddressBookSyncResult result, CancellationToken cancellationToken = default);

    /// <summary>Stores one contact after a successful write to the server (keeps the address book's sync state).</summary>
    Task UpsertContactAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default);

    Task DeleteContactAsync(AddressBookInfo addressBook, string remoteId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContactObject>> GetContactsAsync(Guid connectionId, string addressBookRemoteId, CancellationToken cancellationToken = default);
}
