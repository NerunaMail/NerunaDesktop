using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using CoreFlags = Neruna.Core.Mail.MessageFlags;
using MailFolder = Neruna.Core.Mail.MailFolder;
using MessageSummary = Neruna.Core.Mail.MessageSummary;
using ImapFlags = MailKit.MessageFlags;

namespace Neruna.Providers.Imap;

/// <summary>
/// IMAP for reading, SMTP for sending. Remote ids are IMAP UIDs; the sync state is <c>UIDVALIDITY:UIDNEXT</c>.
/// </summary>
/// <remarks>
/// First iteration: detects flag changes and deletions by fetching flags of all known UIDs.
/// CONDSTORE/QRESYNC (RFC 7162) and IDLE push will replace that for large folders.
/// </remarks>
public sealed class ImapMailProvider(
    Guid connectionId,
    ImapSettings settings,
    ICredentialStore credentials,
    ILogger<ImapMailProvider> logger,
    string? protocolLogDirectory = null,
    int maxInitialMessages = ImapMailProvider.DefaultMaxInitialMessages) : IMailProvider
{
    /// <summary>On first sync only the newest messages are fetched; older ones are loaded on demand (scrolling down).</summary>
    public const int DefaultMaxInitialMessages = 2000;

    private const int FetchBatchSize = 250;

    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate |
        MessageSummaryItems.PreviewText;

    private ImapClient? _imap;

    public MailProviderCapabilities Capabilities =>
        MailProviderCapabilities.Send | MailProviderCapabilities.Flags | MailProviderCapabilities.Move |
        MailProviderCapabilities.Delete | MailProviderCapabilities.ServerSearch | MailProviderCapabilities.Append | MailProviderCapabilities.Push;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        await GetImapAsync(cancellationToken);
        using var smtp = await ConnectSmtpAsync(cancellationToken);
        await smtp.DisconnectAsync(true, cancellationToken);
    }

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        var imap = await GetImapAsync(cancellationToken);
        var result = new List<MailFolder> { ToMailFolder(imap.Inbox, FolderRole.Inbox) };

        foreach (var ns in imap.PersonalNamespaces)
        {
            foreach (var folder in await imap.GetFoldersAsync(ns, StatusItems.None, false, cancellationToken))
            {
                if (folder == imap.Inbox || folder.Attributes.HasFlag(FolderAttributes.NoSelect) || folder.Attributes.HasFlag(FolderAttributes.NonExistent))
                {
                    continue;
                }

                result.Add(ToMailFolder(folder, RoleOf(folder)));
            }
        }

        return result;
    }

    public async Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);

        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);
        var previous = SyncState.Parse(folder.SyncState);
        var isFull = previous is null || previous.Value.UidValidity != imapFolder.UidValidity;

        // Messages marked \Deleted but not yet expunged count as gone: some mail programs delete that way and purge
        // the folder only later (or on exit). They hide such messages, so do we.
        var allUids = await imapFolder.SearchAsync(SearchQuery.NotDeleted, cancellationToken);
        var unread = (await imapFolder.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.NotDeleted), cancellationToken)).Count;

        List<UniqueId> toFetch;
        var flagUpdates = new Dictionary<string, CoreFlags>();
        var removed = new List<string>();

        if (isFull)
        {
            toFetch = allUids.Skip(Math.Max(0, allUids.Count - maxInitialMessages)).ToList();
        }
        else
        {
            var present = allUids.Select(u => u.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
            removed.AddRange(knownRemoteIds.Where(id => !present.Contains(id)));

            // New mail – plus messages that were hidden as \Deleted and have been restored since.
            var known = knownRemoteIds.ToHashSet(StringComparer.Ordinal);
            var oldestKnown = known.Count == 0 ? uint.MaxValue : known.Min(id => uint.Parse(id, CultureInfo.InvariantCulture));
            toFetch = allUids
                .Where(u => u.Id >= previous!.Value.UidNext || (u.Id >= oldestKnown && !known.Contains(u.Id.ToString(CultureInfo.InvariantCulture))))
                .ToList();

            var knownUids = knownRemoteIds.Where(present.Contains).Select(id => new UniqueId(uint.Parse(id, CultureInfo.InvariantCulture))).ToList();
            foreach (var batch in knownUids.Chunk(FetchBatchSize * 4))
            {
                foreach (var summary in await imapFolder.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken))
                {
                    flagUpdates[summary.UniqueId.Id.ToString(CultureInfo.InvariantCulture)] = MapFlags(summary.Flags);
                }
            }
        }

        var added = new List<MessageSummary>(toFetch.Count);
        foreach (var batch in toFetch.Chunk(FetchBatchSize))
        {
            foreach (var summary in await imapFolder.FetchAsync(batch, SummaryItems, cancellationToken))
            {
                added.Add(MapSummary(summary));
            }
        }

        var uidNext = imapFolder.UidNext?.Id ?? (allUids.Count > 0 ? allUids[^1].Id + 1 : 1);
        var newState = new SyncState(imapFolder.UidValidity, uidNext).ToString();

        logger.LogDebug("IMAP {Folder}: full={Full}, fetched={Fetched}, removed={Removed}", folder.RemoteId, isFull, added.Count, removed.Count);
        return new FolderSyncResult(newState, isFull, added, flagUpdates, removed, allUids.Count, unread);
    }

    public async Task<MimeMessage> GetMessageAsync(MailFolder folder, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);
        return await imapFolder.GetMessageAsync(ParseUid(remoteId), cancellationToken);
    }

    public async Task SetFlagsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CoreFlags flags, bool add, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(remoteIds);

        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadWrite, cancellationToken);
        var request = new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, MapFlags(flags)) { Silent = true };
        await imapFolder.StoreAsync(remoteIds.Select(ParseUid).ToList(), request, cancellationToken);
    }

    public async Task MoveAsync(MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(remoteIds);
        ArgumentNullException.ThrowIfNull(target);

        var imap = await GetImapAsync(cancellationToken);
        var destination = await imap.GetFolderAsync(target.RemoteId, cancellationToken);
        var folder = await OpenAsync(source.RemoteId, FolderAccess.ReadWrite, cancellationToken);

        // MailKit uses MOVE (RFC 6851) when available, otherwise COPY + \Deleted + EXPUNGE.
        await folder.MoveToAsync(remoteIds.Select(ParseUid).ToList(), destination, cancellationToken);
    }

    public async Task DeleteAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(remoteIds);

        var imap = await GetImapAsync(cancellationToken);
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadWrite, cancellationToken);
        var uids = remoteIds.Select(ParseUid).ToList();
        await imapFolder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, ImapFlags.Deleted) { Silent = true }, cancellationToken);

        // UID EXPUNGE only removes our messages; plain EXPUNGE would also purge others' \Deleted mails.
        if (imap.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            await imapFolder.ExpungeAsync(uids, cancellationToken);
        }
        else
        {
            await imapFolder.ExpungeAsync(cancellationToken);
        }
    }

    public async Task AppendAsync(MailFolder folder, MimeMessage message, CoreFlags flags, DateTimeOffset? receivedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(message);

        var imap = await GetImapAsync(cancellationToken);
        var destination = await imap.GetFolderAsync(folder.RemoteId, cancellationToken);
        var request = new AppendRequest(message, MapFlags(flags & ~CoreFlags.Deleted));
        if (receivedAt is { } date)
        {
            request.InternalDate = date;
        }

        await destination.AppendAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<MessageSummary>> FetchOlderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);
        var oldest = knownRemoteIds.Select(id => uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : uint.MaxValue).DefaultIfEmpty(uint.MaxValue).Min();
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);
        var older = (await imapFolder.SearchAsync(SearchQuery.NotDeleted, cancellationToken)).Where(u => u.Id < oldest).ToList();
        return await FetchSummariesAsync(imapFolder, older.Skip(Math.Max(0, older.Count - count)).ToList(), cancellationToken);
    }

    public async Task<(IReadOnlyList<MessageSummary> Hits, bool IsTruncated)> SearchAsync(MailFolder folder, MailSearchQuery query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(query);
        SearchQuery search = SearchQuery.NotDeleted;
        if (!string.IsNullOrWhiteSpace(query.From))
        {
            search = search.And(SearchQuery.FromContains(query.From.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Subject))
        {
            search = search.And(SearchQuery.SubjectContains(query.Subject.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            search = search.And(SearchQuery.BodyContains(query.Text.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Anywhere))
        {
            // IMAP TEXT: header and body – sender, recipients, subject and text in one go.
            search = search.And(SearchQuery.MessageContains(query.Anywhere.Trim()));
        }

        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);
        var uids = await imapFolder.SearchAsync(search, cancellationToken);
        var newest = uids.Skip(Math.Max(0, uids.Count - limit)).ToList();
        return (await FetchSummariesAsync(imapFolder, newest, cancellationToken), uids.Count > limit);
    }

    private static async Task<IReadOnlyList<MessageSummary>> FetchSummariesAsync(IMailFolder folder, List<UniqueId> uids, CancellationToken cancellationToken)
    {
        var result = new List<MessageSummary>(uids.Count);
        foreach (var batch in uids.Chunk(FetchBatchSize))
        {
            foreach (var summary in await folder.FetchAsync(batch, SummaryItems, cancellationToken))
            {
                result.Add(MapSummary(summary));
            }
        }

        return result.OrderByDescending(m => m.Date).ToList();
    }

    /// <summary>Servers without IDLE are asked this often.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    private bool _pollingLogged;

    // RFC 2177: re-issue IDLE before 29 minutes, or the server may drop the connection.
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(25);

    public async Task WaitForChangesAsync(MailFolder folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var imap = await GetImapAsync(cancellationToken);
        if (!imap.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            if (!_pollingLogged)
            {
                _pollingLogged = true;
                logger.LogInformation("{Host} does not support IDLE; asking every {Interval}", settings.ImapHost, PollInterval);
            }

            await Task.Delay(PollInterval, cancellationToken);
            return;
        }

        // Mail that arrived before this connection watched the folder is not reported as a change: the first call
        // only opens the folder and returns, so the caller syncs once more – from then on nothing is missed.
        var watched = string.Equals(folder.RemoteId, imap.Inbox.FullName, StringComparison.OrdinalIgnoreCase)
            ? imap.Inbox
            : await imap.GetFolderAsync(folder.RemoteId, cancellationToken);
        var wasOpen = watched.IsOpen;
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);
        if (!wasOpen)
        {
            return;
        }

        using var done = new CancellationTokenSource(IdleLimit);
        void Changed(object? sender, EventArgs e) => done.Cancel();
        imapFolder.CountChanged += Changed;
        imapFolder.MessageExpunged += Changed;
        imapFolder.MessageFlagsChanged += Changed;
        try
        {
            await imap.IdleAsync(done.Token, cancellationToken);
        }
        finally
        {
            imapFolder.CountChanged -= Changed;
            imapFolder.MessageExpunged -= Changed;
            imapFolder.MessageFlagsChanged -= Changed;
        }
    }

    public async Task<MailFolder> CreateFolderAsync(string name, FolderRole role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var imap = await GetImapAsync(cancellationToken);
        var root = imap.GetFolder(imap.PersonalNamespaces[0]);
        var created = await root.CreateAsync(name, true, cancellationToken)
                      ?? throw new InvalidOperationException($"Ordner «{name}» konnte nicht angelegt werden.");
        return ToMailFolder(created, role);
    }

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using (var smtp = await ConnectSmtpAsync(cancellationToken))
        {
            await smtp.SendAsync(message, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }

        // Most IMAP servers do not file sent mail themselves; users expect it in "Sent".
        var imap = await GetImapAsync(cancellationToken);
        if (imap.Capabilities.HasFlag(ImapCapabilities.SpecialUse) && imap.GetFolder(SpecialFolder.Sent) is { } sent)
        {
            await sent.AppendAsync(new AppendRequest(message, ImapFlags.Seen), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_imap is { } imap)
        {
            if (imap.IsConnected)
            {
                try
                {
                    await imap.DisconnectAsync(true);
                }
                catch (Exception ex) when (ex is IOException or ImapProtocolException or ImapCommandException)
                {
                    logger.LogDebug(ex, "Ignoring error while disconnecting");
                }
            }

            imap.Dispose();
            _imap = null;
        }
    }

    private async Task<ImapClient> GetImapAsync(CancellationToken cancellationToken)
    {
        if (_imap is { IsConnected: true, IsAuthenticated: true })
        {
            return _imap;
        }

        var password = await GetPasswordAsync(cancellationToken);
        var imap = new ImapClient(CreateProtocolLogger("imap"));
        try
        {
            await imap.ConnectAsync(settings.ImapHost, settings.ImapPort, MapSecurity(settings.ImapSecurity), cancellationToken);
            await imap.AuthenticateAsync(settings.Username, password, cancellationToken);
        }
        catch
        {
            imap.Dispose();
            throw;
        }

        _imap = imap;
        return imap;
    }

    private async Task<SmtpClient> ConnectSmtpAsync(CancellationToken cancellationToken)
    {
        var password = await GetPasswordAsync(cancellationToken);
        var smtp = new SmtpClient(CreateProtocolLogger("smtp"));
        try
        {
            await smtp.ConnectAsync(settings.SmtpHost, settings.SmtpPort, MapSecurity(settings.SmtpSecurity), cancellationToken);
            await smtp.AuthenticateAsync(settings.Username, password, cancellationToken);
            return smtp;
        }
        catch
        {
            smtp.Dispose();
            throw;
        }
    }

    /// <summary>Raw protocol traces for debugging; passwords are redacted by MailKit.</summary>
    private IProtocolLogger CreateProtocolLogger(string protocol)
    {
        if (protocolLogDirectory is null)
        {
            return new NullProtocolLogger();
        }

        Directory.CreateDirectory(protocolLogDirectory);
        var file = Path.Combine(protocolLogDirectory, string.Create(CultureInfo.InvariantCulture,
            $"{protocol}-{connectionId.ToString("N")[..8]}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.CurrentManagedThreadId}.log"));
        return new ProtocolLogger(file) { RedactSecrets = true };
    }

    private async Task<string> GetPasswordAsync(CancellationToken cancellationToken) =>
        await credentials.GetSecretAsync(connectionId, cancellationToken)
        ?? throw new InvalidOperationException($"No password stored for connection {connectionId}.");

    private async Task<IMailFolder> OpenAsync(string remoteId, FolderAccess access, CancellationToken cancellationToken)
    {
        var imap = await GetImapAsync(cancellationToken);
        var folder = string.Equals(remoteId, imap.Inbox.FullName, StringComparison.OrdinalIgnoreCase)
            ? imap.Inbox
            : await imap.GetFolderAsync(remoteId, cancellationToken);

        if (!folder.IsOpen || (access == FolderAccess.ReadWrite && folder.Access != FolderAccess.ReadWrite))
        {
            await folder.OpenAsync(access, cancellationToken);
        }

        return folder;
    }

    private MailFolder ToMailFolder(IMailFolder folder, FolderRole role)
    {
        var parent = folder.ParentFolder is { FullName.Length: > 0 } p ? p.FullName : null;
        return new MailFolder(connectionId, folder.FullName, folder.Name, parent, role);
    }

    internal static FolderRole RoleOf(IMailFolder folder) => folder.Attributes switch
    {
        var a when a.HasFlag(FolderAttributes.Inbox) => FolderRole.Inbox,
        var a when a.HasFlag(FolderAttributes.Sent) => FolderRole.Sent,
        var a when a.HasFlag(FolderAttributes.Drafts) => FolderRole.Drafts,
        var a when a.HasFlag(FolderAttributes.Trash) => FolderRole.Trash,
        var a when a.HasFlag(FolderAttributes.Junk) => FolderRole.Junk,
        var a when a.HasFlag(FolderAttributes.Archive) => FolderRole.Archive,
        _ => RoleOfName(folder.ParentFolder is { FullName.Length: > 0 } ? null : folder.Name),
    };

    // Servers without SPECIAL-USE (RFC 6154): recognise the usual top-level names.
    internal static FolderRole RoleOfName(string? name) => name?.ToLowerInvariant() switch
    {
        "drafts" or "entwürfe" or "brouillons" or "bozze" => FolderRole.Drafts,
        "sent" or "sent items" or "sent messages" or "sent mail" or "gesendet" or "gesendete elemente" or "gesendete objekte" => FolderRole.Sent,
        "trash" or "deleted items" or "deleted messages" or "papierkorb" or "gelöschte elemente" => FolderRole.Trash,
        "junk" or "spam" or "junk e-mail" or "junk-e-mail" => FolderRole.Junk,
        "archive" or "archiv" => FolderRole.Archive,
        _ => FolderRole.None,
    };

    internal static MessageSummary MapSummary(IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var from = envelope?.From.Mailboxes.FirstOrDefault();

        return new MessageSummary(
            summary.UniqueId.Id.ToString(CultureInfo.InvariantCulture),
            envelope?.MessageId,
            envelope?.InReplyTo,
            envelope?.Subject ?? string.Empty,
            from is null ? null : new MailAddress(from.Name, from.Address),
            envelope?.To.Mailboxes.Select(m => new MailAddress(m.Name, m.Address)).ToList() ?? [],
            envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.MinValue,
            MapFlags(summary.Flags),
            (long)(summary.Size ?? 0),
            HasRealAttachments(summary),
            summary.PreviewText,
            SecurityOf(summary.Body?.ContentType));
    }

    /// <summary>
    /// Same idea as MessageContent: inline-disposition files, TNEF and attached mails count; signature blobs and
    /// embedded images (e.g. logos in signatures, referenced via Content-Id) do not.
    /// </summary>
    internal static bool HasRealAttachments(IMessageSummary summary)
    {
        if (summary.Body is null)
        {
            return false;
        }

        foreach (var part in summary.BodyParts)
        {
            var type = part.ContentType;
            // S/MIME signature blobs and the encrypted body itself (smime.p7m) are not attachments for the user.
            if (type.IsMimeType("application", "pkcs7-signature") || type.IsMimeType("application", "x-pkcs7-signature")
                || type.IsMimeType("application", "pkcs7-mime") || type.IsMimeType("application", "x-pkcs7-mime"))
            {
                continue;
            }

            if (part.IsAttachment || type.IsMimeType("application", "ms-tnef") || type.IsMimeType("message", "rfc822"))
            {
                return true;
            }

            var named = part.FileName is not null || type.Name is not null;
            var embeddedImage = type.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) && part.ContentId is not null;
            if (named && !embeddedImage && !type.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static MessageSecurity SecurityOf(ContentType? type)
    {
        if (type is null)
        {
            return MessageSecurity.None;
        }

        if (type.IsMimeType("multipart", "signed"))
        {
            return MessageSecurity.Signed;
        }

        if (type.IsMimeType("application", "pkcs7-mime") || type.IsMimeType("application", "x-pkcs7-mime"))
        {
            var smimeType = type.Parameters["smime-type"]?.ToLowerInvariant();
            return smimeType is "signed-data" or "certs-only" ? MessageSecurity.Signed : MessageSecurity.Encrypted;
        }

        return MessageSecurity.None;
    }

    internal static CoreFlags MapFlags(ImapFlags? flags)
    {
        var f = flags ?? ImapFlags.None;
        var result = CoreFlags.None;
        if (f.HasFlag(ImapFlags.Seen)) result |= CoreFlags.Seen;
        if (f.HasFlag(ImapFlags.Answered)) result |= CoreFlags.Answered;
        if (f.HasFlag(ImapFlags.Flagged)) result |= CoreFlags.Flagged;
        if (f.HasFlag(ImapFlags.Draft)) result |= CoreFlags.Draft;
        if (f.HasFlag(ImapFlags.Deleted)) result |= CoreFlags.Deleted;
        return result;
    }

    internal static ImapFlags MapFlags(CoreFlags flags)
    {
        var result = ImapFlags.None;
        if (flags.HasFlag(CoreFlags.Seen)) result |= ImapFlags.Seen;
        if (flags.HasFlag(CoreFlags.Answered)) result |= ImapFlags.Answered;
        if (flags.HasFlag(CoreFlags.Flagged)) result |= ImapFlags.Flagged;
        if (flags.HasFlag(CoreFlags.Draft)) result |= ImapFlags.Draft;
        if (flags.HasFlag(CoreFlags.Deleted)) result |= ImapFlags.Deleted;
        return result;
    }

    private static SecureSocketOptions MapSecurity(SocketSecurity security) => security switch
    {
        SocketSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SocketSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    private static UniqueId ParseUid(string remoteId) => new(uint.Parse(remoteId, CultureInfo.InvariantCulture));

    internal readonly record struct SyncState(uint UidValidity, uint UidNext)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{UidValidity}:{UidNext}");

        public static SyncState? Parse(string? value)
        {
            var parts = value?.Split(':');
            return parts is [var validity, var next]
                   && uint.TryParse(validity, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                   && uint.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? new SyncState(v, n)
                : null;
        }
    }
}
