using MimeKit;

namespace Neruna.Core.Mail;

[Flags]
public enum MailProviderCapabilities
{
    None = 0,
    Send = 1,
    Flags = 2,
    Move = 4,
    Delete = 8,
    ServerSearch = 16,
    Push = 32,

    /// <summary>Can store a complete message in a folder (IMAP APPEND) – needed to move mail between accounts.</summary>
    Append = 64,
}

/// <summary>
/// A mail backend (IMAP/SMTP, later EWS, Graph, JMAP …) bound to one connection.
/// Identifiers (<see cref="MailFolder.RemoteId"/>, <see cref="MessageSummary.RemoteId"/>) and
/// <see cref="MailFolder.SyncState"/> are opaque strings owned by the provider; the rest of the app never interprets them.
/// Messages always travel as MIME so storage and UI are protocol-neutral.
/// </summary>
public interface IMailProvider : IAsyncDisposable
{
    MailProviderCapabilities Capabilities { get; }

    /// <summary>Connects and authenticates; used to validate settings during account setup.</summary>
    Task TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings a folder up to date relative to <paramref name="folder"/>.<see cref="MailFolder.SyncState"/>.
    /// </summary>
    /// <param name="knownRemoteIds">Message ids currently stored locally, used to detect server-side deletions.</param>
    Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default);

    Task<MimeMessage> GetMessageAsync(MailFolder folder, string remoteId, CancellationToken cancellationToken = default);

    Task SetFlagsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default);

    /// <exception cref="NotSupportedException">The provider lacks <see cref="MailProviderCapabilities.Move"/>.</exception>
    Task MoveAsync(MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default);

    /// <summary>Deletes permanently (IMAP: \Deleted + EXPUNGE). Moving to the trash is <see cref="MoveAsync"/>.</summary>
    /// <exception cref="NotSupportedException">The provider lacks <see cref="MailProviderCapabilities.Delete"/>.</exception>
    Task DeleteAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default);

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default);

    /// <summary>Stores a complete message in <paramref name="folder"/> (IMAP APPEND), keeping flags and received date.</summary>
    /// <exception cref="NotSupportedException">The provider lacks <see cref="MailProviderCapabilities.Append"/>.</exception>
    Task AppendAsync(MailFolder folder, MimeMessage message, MessageFlags flags, DateTimeOffset? receivedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns when the server reports a change in <paramref name="folder"/> (new, removed or changed messages – IMAP
    /// IDLE) or after a while without one; the caller then syncs the folder. Without push support it simply waits a
    /// polling interval. Uses the provider's own connection, so keep this instance for the whole watch.
    /// </summary>
    Task WaitForChangesAsync(MailFolder folder, CancellationToken cancellationToken);

    /// <summary>Creates a top-level folder, e.g. "Drafts" on a server that has none.</summary>
    /// <exception cref="NotSupportedException">The provider lacks <see cref="MailProviderCapabilities.Append"/>.</exception>
    Task<MailFolder> CreateFolderAsync(string name, FolderRole role, CancellationToken cancellationToken = default);
}

/// <param name="IsFullResync">The previous state is void (e.g. IMAP UIDVALIDITY changed): replace the folder's contents.</param>
/// <param name="FlagUpdates">Flag changes of already known messages, without re-downloading their summaries.</param>
public sealed record FolderSyncResult(
    string NewSyncState,
    bool IsFullResync,
    IReadOnlyList<MessageSummary> AddedOrChanged,
    IReadOnlyDictionary<string, MessageFlags> FlagUpdates,
    IReadOnlyList<string> RemovedRemoteIds,
    int TotalCount,
    int UnreadCount);
