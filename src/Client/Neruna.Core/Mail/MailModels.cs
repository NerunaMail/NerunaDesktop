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
/// <summary>
/// What to search for; empty fields are ignored, all given ones must match. <paramref name="Anywhere"/> matches
/// sender, recipients, subject or text (the quick search).
/// </summary>
public sealed record MailSearchQuery(string? From = null, string? Subject = null, string? Text = null, string? Anywhere = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(From) && string.IsNullOrWhiteSpace(Subject)
                           && string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Anywhere);

    /// <summary>Without the server: what the stored summaries can answer (the text only through the preview).</summary>
    public bool Matches(MessageSummary message)
    {
        ArgumentNullException.ThrowIfNull(message);
        static bool Has(string? value, string? query) =>
            string.IsNullOrWhiteSpace(query) || (value?.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false);
        var from = message.From is { } f ? f.DisplayText + " " + f.Address : null;
        var anywhere = string.Join(" ", from, message.Subject, message.Preview, string.Join(" ", message.To.Select(t => t.DisplayText + " " + t.Address)));
        return Has(from, From) && Has(message.Subject, Subject) && Has(message.Preview, Text) && Has(anywhere, Anywhere);
    }
}

/// <summary>A search hit: the message and the folder it is in.</summary>
public sealed record MailSearchHit(MailFolder Folder, MessageSummary Message);

/// <param name="FailedFolders">Folders that could not be searched (server error); the others are in the result.</param>
/// <param name="IsTruncated">More messages matched than are returned.</param>
public sealed record MailSearchResult(IReadOnlyList<MailSearchHit> Hits, IReadOnlyList<MailFolder> FailedFolders, bool IsTruncated);

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
