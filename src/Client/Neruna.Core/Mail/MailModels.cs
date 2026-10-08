namespace Neruna.Core.Mail;

public enum FolderRole
{
    None,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
}

/// <param name="ConnectionId">The mail connection this folder belongs to.</param>
/// <param name="RemoteId">Provider-owned id (IMAP: full folder name).</param>
/// <param name="ParentRemoteId">Null for top-level folders.</param>
/// <param name="SyncState">Provider-owned, opaque; null before the first sync.</param>
public sealed record MailFolder(
    Guid ConnectionId,
    string RemoteId,
    string Name,
    string? ParentRemoteId,
    FolderRole Role,
    int TotalCount = 0,
    int UnreadCount = 0,
    string? SyncState = null);

[Flags]
public enum MessageFlags
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Draft = 8,
    Deleted = 16,
    Forwarded = 32,
}

/// <summary>S/MIME protection visible from the message structure, before it is downloaded.</summary>
[Flags]
public enum MessageSecurity
{
    None = 0,
    Signed = 1,
    Encrypted = 2,
}

public sealed record MailAddress(string? Name, string Address)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

/// <summary>What the message list needs, without downloading the message body.</summary>
public sealed record MessageSummary(
    string RemoteId,
    string? MessageId,
    string? InReplyTo,
    string Subject,
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    DateTimeOffset Date,
    MessageFlags Flags,
    long Size,
    bool HasAttachments,
    string? Preview,
    MessageSecurity Security = MessageSecurity.None);
