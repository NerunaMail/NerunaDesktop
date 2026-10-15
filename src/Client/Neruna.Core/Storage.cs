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

    /// <summary>
    /// Messages of the folder dated on or after <paramref name="since"/> (null: all) whose content is not on this
    /// device yet, newest first – for keeping them offline. Remote id and size.
    /// </summary>
    Task<IReadOnlyList<(string RemoteId, long Size)>> GetMessagesWithoutContentAsync(Guid connectionId, string folderRemoteId, DateTimeOffset? since, int take, CancellationToken cancellationToken = default);

    /// <summary>Removes the downloaded content of messages dated before <paramref name="before"/>; they stay in the list.</summary>
    Task<int> RemoveMessageContentBeforeAsync(Guid connectionId, string folderRemoteId, DateTimeOffset before, CancellationToken cancellationToken = default);
}

/// <summary>The local database file: its size and making it smaller (VACUUM).</summary>
public interface IStorageMaintenance
{
    /// <summary>Bytes of the database (with its write-ahead log).</summary>
    long DatabaseSize { get; }

    /// <summary>
    /// Gives free space back to the disk – left by deleted messages, accounts or index entries; SQLite reuses it
    /// but does not shrink the file by itself. Takes a while with a large database; nothing else should write meanwhile.
    /// </summary>
    Task CompactAsync(CancellationToken cancellationToken = default);
}

/// <summary>What the search index keeps of a message (a text not on this device: null).</summary>
public sealed record SearchDocument(string Sender, string Recipients, string Subject, string? Body, string? Attachments);

/// <summary>A message the indexer still has to do; <paramref name="HasContent"/>: its MIME is on this device.</summary>
public sealed record SearchWork(long Id, MailFolder Folder, MessageSummary Summary, bool HasContent);

/// <param name="Messages">All messages of the connection.</param>
/// <param name="Indexed">In the index (at least sender, subject, recipients).</param>
/// <param name="WithText">In the index with their text.</param>
public sealed record SearchIndexStatus(int Messages, int Indexed, int WithText);

/// <summary>The local full-text index of the mail (built in the background by <see cref="Mail.MailSearchIndexer"/>).</summary>
public interface IMailSearchIndex
{
    /// <summary>Messages of the given connections at <paramref name="level"/> (see <c>SearchLevel</c>), newest first.</summary>
    Task<IReadOnlyList<SearchWork>> GetSearchWorkAsync(IReadOnlyCollection<Guid> connections, int level, int take, CancellationToken cancellationToken = default);

    /// <summary>Puts the messages into the index (replacing what was there) at their new level.</summary>
    Task SaveSearchEntriesAsync(IReadOnlyList<(long Id, SearchDocument Document, int Level)> entries, CancellationToken cancellationToken = default);

    /// <summary>The newest messages matching <paramref name="match"/> (FTS5 syntax) in the given folders.</summary>
    Task<IReadOnlyList<MailSearchHit>> SearchIndexAsync(string match, IReadOnlyCollection<MailFolder> folders, int limit, CancellationToken cancellationToken = default);

    Task<SearchIndexStatus> GetSearchStatusAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>Removes the connection's messages from the index (indexing switched off for its account).</summary>
    Task ClearSearchIndexAsync(Guid connectionId, CancellationToken cancellationToken = default);
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
