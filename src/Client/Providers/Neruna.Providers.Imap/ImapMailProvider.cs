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
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Imap;

/// <summary>
/// IMAP for reading, SMTP for sending. Remote ids are IMAP UIDs; the sync state is <c>UIDVALIDITY:UIDNEXT</c>.
/// </summary>
/// <remarks>
/// First iteration: detects flag changes and deletions by fetching flags of all known UIDs.
/// CONDSTORE/QRESYNC (RFC 7162) and IDLE push will replace that for large folders.
/// </remarks>
public sealed partial class ImapMailProvider(
    Guid connectionId,
    ImapSettings settings,
    ICredentialStore credentials,
    ILogger<ImapMailProvider> logger,
    string? protocolLogDirectory = null,
    int maxInitialMessages = ImapMailProvider.DefaultMaxInitialMessages) : IMailProvider, IAutoReplyProvider
{
    /// <summary>On first sync only the newest messages are fetched; older ones are loaded on demand (scrolling down).</summary>
    public const int DefaultMaxInitialMessages = 2000;

    private const int FetchBatchSize = 250;

    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate |
        MessageSummaryItems.PreviewText;

    private ImapClient? _imap;
    private IMailFolder? _selected;

    public MailProviderCapabilities Capabilities =>
        MailProviderCapabilities.Send | MailProviderCapabilities.Flags | MailProviderCapabilities.Move |
        MailProviderCapabilities.Delete | MailProviderCapabilities.ServerSearch | MailProviderCapabilities.Append | MailProviderCapabilities.Push |
        MailProviderCapabilities.Subscriptions | MailProviderCapabilities.SearchText;

    // A text part larger than this is not taken into the index (a log file sent as body, a huge newsletter).
    private const long MaxSearchTextBytes = 1024 * 1024;

    public async Task<IReadOnlyDictionary<string, MessageSearchText>> GetSearchTextsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(remoteIds);
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);

        // The structure first (one request), then only the text part of each – BODY.PEEK: nothing is marked read.
        var summaries = await imapFolder.FetchAsync(remoteIds.Select(ParseUid).ToList(), new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure), cancellationToken);
        var result = new Dictionary<string, MessageSearchText>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            var names = summary.Attachments.Select(a => a.FileName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            string? text = null;
            if ((summary.TextBody ?? summary.HtmlBody) is { } part && part.Octets <= MaxSearchTextBytes)
            {
                // The part's raw content (BODY.PEEK[n]), decoded by what the structure says – servers differ in
                // what they send for the part's own MIME header.
                await using var raw = await imapFolder.GetStreamAsync(summary.UniqueId, part.PartSpecifier, cancellationToken);
                var body = new TextPart(part.ContentType.MediaSubtype)
                {
                    Content = new MimeContent(raw, MimeKit.Utils.MimeUtils.TryParse(part.ContentTransferEncoding, out ContentEncoding encoding) ? encoding : ContentEncoding.Default),
                };
                if (part.ContentType.Charset is { Length: > 0 } charset)
                {
                    body.ContentType.Charset = charset;
                }

                text = body.IsHtml ? HtmlText.ToPlainText(body.Text) : body.Text;
            }

            result[summary.UniqueId.Id.ToString(CultureInfo.InvariantCulture)] = new MessageSearchText(text, names.Count == 0 ? null : string.Join(" ", names));
        }

        return result;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        await GetImapAsync(cancellationToken);
        using var smtp = await ConnectSmtpAsync(cancellationToken);
        await smtp.DisconnectAsync(true, cancellationToken);
    }

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        [.. (await GetSubscriptionsAsync(cancellationToken)).Where(s => s.Subscribed).Select(s => s.Folder)];

    /// <summary>
    /// All folders and whether they are subscribed. A server where nothing is subscribed (some never use it) counts as
    /// "everything subscribed" – otherwise only the inbox would be left. Special folders are always shown.
    /// </summary>
    public async Task<IReadOnlyList<FolderSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var imap = await GetImapAsync(cancellationToken);
        var all = new List<(IMailFolder Folder, FolderRole Role)> { (imap.Inbox, FolderRole.Inbox) };
        var subscribed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in imap.PersonalNamespaces)
        {
            foreach (var folder in await imap.GetFoldersAsync(ns, StatusItems.None, false, cancellationToken))
            {
                if (folder == imap.Inbox || folder.Attributes.HasFlag(FolderAttributes.NoSelect) || folder.Attributes.HasFlag(FolderAttributes.NonExistent))
                {
                    continue;
                }

                all.Add((folder, RoleOf(folder)));
            }

            foreach (var folder in await imap.GetFoldersAsync(ns, StatusItems.None, true, cancellationToken))
            {
                subscribed.Add(folder.FullName);
            }
        }

        // One special folder per kind: a mailbox used by several programs has "Drafts" and "DRAFTS", "Sent" and "Sent
        // Items" … Each would be shown as "Entwürfe" or "Gesendete Elemente", and drafts and sent mail went to whichever
        // came first. The others are ordinary folders under their own names (and can be deleted).
        var counts = new Dictionary<IMailFolder, int>();
        foreach (var group in all.Where(f => f.Role != FolderRole.None && !IsMarked(f.Folder, f.Role)).GroupBy(f => f.Role).Where(g => g.Count() > 1 && !all.Any(a => a.Role == g.Key && IsMarked(a.Folder, a.Role))))
        {
            foreach (var (folder, _) in group)
            {
                try
                {
                    await folder.StatusAsync(StatusItems.Count, cancellationToken);
                    counts[folder] = folder.Count;
                }
                catch (ImapCommandException)
                {
                    counts[folder] = 0;
                }
            }
        }

        var roles = UniqueRoles([.. all.Select(f => new RoleCandidate(f.Role, IsMarked(f.Folder, f.Role), counts.GetValueOrDefault(f.Folder)))]);
        all = [.. all.Select((f, i) => (f.Folder, roles[i]))];

        var everything = subscribed.Count == 0;
        return [.. all.Select(f =>
        {
            var required = f.Role is FolderRole.Inbox or FolderRole.Sent or FolderRole.Drafts or FolderRole.Trash;
            return new FolderSubscription(ToMailFolder(f.Folder, f.Role), everything || required || subscribed.Contains(f.Folder.FullName), required);
        })];
    }

    public async Task SetSubscriptionsAsync(IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        var imap = await GetImapAsync(cancellationToken);
        var wanted = remoteIds.ToHashSet(StringComparer.Ordinal);
        var subscribed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in imap.PersonalNamespaces)
        {
            foreach (var folder in await imap.GetFoldersAsync(ns, StatusItems.None, true, cancellationToken))
            {
                subscribed.Add(folder.FullName);
            }
        }

        foreach (var subscription in await GetSubscriptionsAsync(cancellationToken))
        {
            var id = subscription.Folder.RemoteId;
            var want = subscription.Required || wanted.Contains(id);
            if (want == subscribed.Contains(id))
            {
                continue;
            }

            var folder = await imap.GetFolderAsync(id, cancellationToken);
            if (want)
            {
                await folder.SubscribeAsync(cancellationToken);
            }
            else
            {
                await folder.UnsubscribeAsync(cancellationToken);
            }
        }
    }

    public async Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);

        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);
        var previous = SyncState.Parse(folder.SyncState);
        var isFull = previous is null || previous.Value.UidValidity != imapFolder.UidValidity;

        // CONDSTORE (RFC 7162): the folder's change counter. Same counter, same number of messages and no new UID –
        // nothing happened: a large folder costs one EXAMINE every few minutes instead of the flags of every message.
        if (!isFull && previous!.Value.ModSeq is not null)
        {
            await (await GetImapAsync(cancellationToken)).NoOpAsync(cancellationToken); // a folder already open: its counters up to date
        }

        var modSeq = imapFolder.HighestModSeq;
        if (!isFull && modSeq > 0 && previous!.Value.ModSeq == modSeq && previous.Value.Exists == imapFolder.Count
            && imapFolder.UidNext?.Id == previous.Value.UidNext && folder.SyncState is { } unchanged)
        {
            return new FolderSyncResult(unchanged, false, [], new Dictionary<string, CoreFlags>(), [], folder.TotalCount, folder.UnreadCount);
        }

        // Messages marked \Deleted but not yet expunged count as gone: some mail programs delete that way and purge
        // the folder only later (or on exit). They hide such messages, so do we.
        var allUids = await imapFolder.SearchAsync(SearchQuery.NotDeleted, cancellationToken);
        var unread = (await imapFolder.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.NotDeleted), cancellationToken)).Count;

        List<UniqueId> toFetch;
        var flagUpdates = new Dictionary<string, CoreFlags>();
        var removed = new List<string>();

        uint floor;
        if (isFull)
        {
            toFetch = allUids.Skip(Math.Max(0, allUids.Count - maxInitialMessages)).ToList();
            floor = toFetch.Count > 0 ? toFetch[0].Id : imapFolder.UidNext?.Id ?? 1;
        }
        else
        {
            var present = allUids.Select(u => u.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
            removed.AddRange(knownRemoteIds.Where(id => !present.Contains(id)));

            // New mail – plus messages that were hidden as \Deleted and have been restored since. "Restored" means anywhere
            // in the synced range, which starts at the stored floor: the oldest message still known is no measure, as the
            // restored message may itself have been the oldest (or the only) one.
            var known = knownRemoteIds.ToHashSet(StringComparer.Ordinal);
            var oldestKnown = known.Count == 0 ? uint.MaxValue : known.Min(id => uint.Parse(id, CultureInfo.InvariantCulture));
            floor = Math.Min(previous!.Value.Floor ?? (known.Count == 0 ? previous.Value.UidNext : oldestKnown), oldestKnown); // older ones loaded on request widen it
            toFetch = allUids
                .Where(u => u.Id >= previous.Value.UidNext || (u.Id >= floor && !known.Contains(u.Id.ToString(CultureInfo.InvariantCulture))))
                .ToList();

            // With CONDSTORE only the flags changed since the last sync, otherwise those of every known message.
            var knownUids = knownRemoteIds.Where(present.Contains).Select(id => new UniqueId(uint.Parse(id, CultureInfo.InvariantCulture))).ToList();
            var since = modSeq > 0 && previous.Value.ModSeq is { } lastSeq && lastSeq <= modSeq ? lastSeq : (ulong?)null;
            foreach (var batch in knownUids.Chunk(FetchBatchSize * 4))
            {
                var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags) { ChangedSince = since };
                foreach (var summary in await imapFolder.FetchAsync(batch, request, cancellationToken))
                {
                    flagUpdates[summary.UniqueId.Id.ToString(CultureInfo.InvariantCulture)] = MapFlags(summary.Flags, summary.Keywords);
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
        var newState = new SyncState(imapFolder.UidValidity, uidNext, floor, modSeq > 0 ? modSeq : null, modSeq > 0 ? imapFolder.Count : null).ToString();

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
        var uids = remoteIds.Select(ParseUid).ToList();
        var system = MapFlags(flags);
        if (system != ImapFlags.None)
        {
            await imapFolder.StoreAsync(uids, new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, system) { Silent = true }, cancellationToken);
        }

        // "Weitergeleitet" is the keyword $Forwarded (RFC 5788), not a system flag; only where the folder keeps keywords.
        if (flags.HasFlag(CoreFlags.Forwarded) && AcceptsKeyword(imapFolder, ForwardedKeyword))
        {
            var request = new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, ImapFlags.None) { Silent = true };
            request.Keywords.Add(ForwardedKeyword);
            await imapFolder.StoreAsync(uids, request, cancellationToken);
        }
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

    public async Task MarkAllReadAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadWrite, cancellationToken);
        var unread = await imapFolder.SearchAsync(SearchQuery.NotSeen, cancellationToken);
        if (unread.Count > 0)
        {
            await imapFolder.StoreAsync(unread, new StoreFlagsRequest(StoreAction.Add, ImapFlags.Seen) { Silent = true }, cancellationToken);
        }
    }

    public async Task EmptyAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadWrite, cancellationToken);
        var all = await imapFolder.SearchAsync(SearchQuery.All, cancellationToken);
        if (all.Count > 0)
        {
            await DeleteAsync(folder, [.. all.Select(u => u.Id.ToString(CultureInfo.InvariantCulture))], cancellationToken);
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
                      ?? throw new InvalidOperationException(F("Ordner «{0}» konnte nicht angelegt werden.", name));
        return ToMailFolder(created, role);
    }

    // Separators of any server (".", "/"), the list wildcards and quotes and backslashes, which servers treat apart.
    public string ForbiddenFolderNameCharacters => "./\\%*\"";

    public async Task<MailFolder> CreateSubfolderAsync(string name, MailFolder? parent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var imap = await GetImapAsync(cancellationToken);
        var root = parent is null ? imap.GetFolder(imap.PersonalNamespaces[0]) : await imap.GetFolderAsync(parent.RemoteId, cancellationToken);
        var created = await root.CreateAsync(name, true, cancellationToken)
                      ?? throw new InvalidOperationException(F("Ordner «{0}» konnte nicht angelegt werden.", name));
        if (await UsesSubscriptionsAsync(imap, cancellationToken))
        {
            await created.SubscribeAsync(cancellationToken);
        }

        return ToMailFolder(created, FolderRole.None);
    }

    public async Task RenameFolderAsync(MailFolder folder, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var imap = await GetImapAsync(cancellationToken);
        var subscriptions = await UsesSubscriptionsAsync(imap, cancellationToken);
        var imapFolder = await imap.GetFolderAsync(folder.RemoteId, cancellationToken);
        await LeaveAsync(imap, imapFolder, cancellationToken);

        // Subscriptions belong to names: the old ones go, the new names (with the subfolders) are subscribed.
        var tree = subscriptions ? await SelfAndSubfoldersAsync(imapFolder, cancellationToken) : [];
        foreach (var old in tree)
        {
            await old.UnsubscribeAsync(cancellationToken);
        }

        var parent = imapFolder.ParentFolder ?? imap.GetFolder(imap.PersonalNamespaces[0]);
        await imapFolder.RenameAsync(parent, name, cancellationToken);
        if (subscriptions)
        {
            foreach (var renamed in await SelfAndSubfoldersAsync(imapFolder, cancellationToken))
            {
                await renamed.SubscribeAsync(cancellationToken);
            }
        }
    }

    public async Task DeleteFolderAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var imap = await GetImapAsync(cancellationToken);
        var imapFolder = await imap.GetFolderAsync(folder.RemoteId, cancellationToken);
        await LeaveAsync(imap, imapFolder, cancellationToken);

        if (imapFolder.IsSubscribed)
        {
            await imapFolder.UnsubscribeAsync(cancellationToken);
        }

        await imapFolder.DeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAllRemoteIdsAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var imapFolder = await OpenAsync(folder.RemoteId, FolderAccess.ReadOnly, cancellationToken);
        return [.. (await imapFolder.SearchAsync(SearchQuery.NotDeleted, cancellationToken)).Select(u => u.Id.ToString(CultureInfo.InvariantCulture))];
    }

    // Renaming or deleting the folder this connection has selected: the inbox is selected instead. Closing alone is
    // not enough everywhere – without UNSELECT some servers keep it selected (GreenMail then drops the connection).
    private static async Task LeaveAsync(ImapClient imap, IMailFolder folder, CancellationToken cancellationToken)
    {
        if (folder.IsOpen)
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        }
    }

    // A server where nothing is subscribed shows every folder (see GetSubscriptionsAsync) – subscribing one there
    // would hide all the others.
    private static async Task<bool> UsesSubscriptionsAsync(ImapClient imap, CancellationToken cancellationToken)
    {
        foreach (var ns in imap.PersonalNamespaces)
        {
            if ((await imap.GetFoldersAsync(ns, StatusItems.None, true, cancellationToken)).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<List<IMailFolder>> SelfAndSubfoldersAsync(IMailFolder folder, CancellationToken cancellationToken)
    {
        var result = new List<IMailFolder> { folder };
        foreach (var child in await folder.GetSubfoldersAsync(false, cancellationToken))
        {
            result.AddRange(await SelfAndSubfoldersAsync(child, cancellationToken));
        }

        return result;
    }

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using (var smtp = await ConnectSmtpAsync(cancellationToken))
        {
            await smtp.SendAsync(message, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }

        // Most IMAP servers do not file sent mail themselves; users expect it in "Sent". Proton Mail Bridge does.
        if (settings.ServerSavesSent)
        {
            return;
        }

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
                    // Not leaving another folder selected: some servers (GreenMail) keep the logged-out session on it
                    // and fail when that folder is deleted later.
                    if (_selected is { IsOpen: true } selected && selected != imap.Inbox)
                    {
                        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
                    }

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
        var imap = new ImapClient(CreateProtocolLogger("imap")) { ServerCertificateValidationCallback = settings.AcceptAnyCertificate ? TlsCertificates.For(acceptAny: true) : TlsCertificates.For(settings.Raw) };
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
        var smtp = new SmtpClient(CreateProtocolLogger("smtp")) { ServerCertificateValidationCallback = settings.AcceptAnyCertificate ? TlsCertificates.For(acceptAny: true) : TlsCertificates.For(settings.Raw) };
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

        _selected = folder;
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

    // Marked by the server (SPECIAL-USE, RFC 6154) – not just recognised by its name.
    private static bool IsMarked(IMailFolder folder, FolderRole role) => role switch
    {
        FolderRole.Inbox => true,
        FolderRole.Sent => folder.Attributes.HasFlag(FolderAttributes.Sent),
        FolderRole.Drafts => folder.Attributes.HasFlag(FolderAttributes.Drafts),
        FolderRole.Trash => folder.Attributes.HasFlag(FolderAttributes.Trash),
        FolderRole.Junk => folder.Attributes.HasFlag(FolderAttributes.Junk),
        FolderRole.Archive => folder.Attributes.HasFlag(FolderAttributes.Archive),
        _ => false,
    };

    internal readonly record struct RoleCandidate(FolderRole Role, bool Marked, int Count);

    /// <summary>
    /// The role of each folder with only one folder per role: the one the server marks; else of those recognised by
    /// name the one with the most mail (the one the other programs use), the first on a tie. The rest: no role.
    /// </summary>
    internal static IReadOnlyList<FolderRole> UniqueRoles(IReadOnlyList<RoleCandidate> folders)
    {
        var result = folders.Select(f => f.Role).ToArray();
        foreach (var group in folders.Select((f, i) => (Folder: f, Index: i)).Where(f => f.Folder.Role != FolderRole.None).GroupBy(f => f.Folder.Role))
        {
            var keep = group.OrderByDescending(f => f.Folder.Marked).ThenByDescending(f => f.Folder.Count).ThenBy(f => f.Index).First().Index;
            foreach (var (_, index) in group.Where(f => f.Index != keep))
            {
                result[index] = FolderRole.None;
            }
        }

        return result;
    }

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
            MapFlags(summary.Flags, summary.Keywords),
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

    /// <summary>Keyword for forwarded messages (RFC 5788; also used by other mail programs).</summary>
    internal const string ForwardedKeyword = "$Forwarded";

    private static bool AcceptsKeyword(IMailFolder folder, string keyword) =>
        folder.PermanentFlags.HasFlag(ImapFlags.UserDefined) || folder.PermanentKeywords.Contains(keyword);

    internal static CoreFlags MapFlags(ImapFlags? flags, IEnumerable<string>? keywords = null)
    {
        var f = flags ?? ImapFlags.None;
        var result = CoreFlags.None;
        if (keywords?.Contains(ForwardedKeyword, StringComparer.OrdinalIgnoreCase) == true) result |= CoreFlags.Forwarded;
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

    /// <summary>
    /// "UIDVALIDITY:UIDNEXT:FLOOR[:MODSEQ:EXISTS]" – floor: the oldest UID of the synced range (missing in states before
    /// 0.1.16); MODSEQ and EXISTS (CONDSTORE servers, since 0.8.8): the change counter and the message count at the sync.
    /// </summary>
    internal readonly record struct SyncState(uint UidValidity, uint UidNext, uint? Floor = null, ulong? ModSeq = null, int? Exists = null)
    {
        public override string ToString() => (Floor, ModSeq, Exists) switch
        {
            ({ } floor, { } modSeq, { } exists) => string.Create(CultureInfo.InvariantCulture, $"{UidValidity}:{UidNext}:{floor}:{modSeq}:{exists}"),
            ({ } floor, _, _) => string.Create(CultureInfo.InvariantCulture, $"{UidValidity}:{UidNext}:{floor}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{UidValidity}:{UidNext}"),
        };

        public static SyncState? Parse(string? value)
        {
            var parts = value?.Split(':');
            if (parts is not ([_, _] or [_, _, _] or [_, _, _, _, _])
                || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            uint? floor = parts.Length >= 3 && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : null;
            return parts.Length == 5 && floor is not null
                && ulong.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var e)
                    ? new SyncState(v, n, floor, m, e)
                    : new SyncState(v, n, floor);
        }
    }
}
