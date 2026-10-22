using System.Globalization;
using System.Xml.Linq;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Providers.Ews;

/// <summary>
/// Mail through Exchange Web Services. Folder and message ids are EWS ids; a folder's sync state is EWS's
/// SyncFolderItems state. The first sync takes the newest <see cref="InitialMessages"/> (older ones with "load more"),
/// messages travel as MIME. Exchange files sent mail in "Gesendete Elemente" itself. No push yet: polled every minute.
/// </summary>
internal sealed partial class EwsMailProvider(ServiceConnection connection, EwsConnectionFactory connections) : IMailProvider
{
    public const int InitialMessages = 2000;

    // MAPI properties: how the message was last answered (102 reply, 103 reply all, 104 forward) and the matching icon;
    // the message flags (1 read, 8 unsent = draft) and the delivery time – both settable only when a message is created.
    private const int LastVerbTag = 0x1081;
    private const int IconTag = 0x1080;
    private const int MessageFlagsTag = 0x0E07;
    private const int DeliveryTimeTag = 0x0E06;

    private static readonly (string WellKnown, FolderRole Role)[] Roles =
    [
        ("inbox", FolderRole.Inbox), ("sentitems", FolderRole.Sent), ("drafts", FolderRole.Drafts),
        ("deleteditems", FolderRole.Trash), ("junkemail", FolderRole.Junk),
    ];

    private EwsClient? _client;

    public MailProviderCapabilities Capabilities =>
        MailProviderCapabilities.Send | MailProviderCapabilities.Flags | MailProviderCapabilities.Move | MailProviderCapabilities.Delete |
        MailProviderCapabilities.ServerSearch | MailProviderCapabilities.Append | MailProviderCapabilities.SearchText;

    // A body longer than this is cut for the index.
    private const int MaxSearchTextLength = 200_000;

    /// <summary>For the search index: the body as text (Exchange converts HTML) and the attachments' names – no content, nothing marked read.</summary>
    public async Task<IReadOnlyDictionary<string, MessageSearchText>> GetSearchTextsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        var client = await ClientAsync(cancellationToken);
        var items = await EwsOperations.GetItemsAsync(client, remoteIds, EwsOperations.Shape(["item:Body", "item:Attachments"], bodyType: "Text"), cancellationToken, batch: 50);
        var result = new Dictionary<string, MessageSearchText>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Element(X.T + "ItemId")?.Attribute("Id")?.Value is not { } id)
            {
                continue;
            }

            var body = item.Val("Body");
            var names = item.Element(X.T + "Attachments")?.Elements().Select(a => a.Val("Name")).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];
            result[id] = new MessageSearchText(body is { Length: > MaxSearchTextLength } ? body[..MaxSearchTextLength] : body, names.Count == 0 ? null : string.Join(" ", names));
        }

        return result;
    }

    private async Task<EwsClient> ClientAsync(CancellationToken cancellationToken) =>
        _client ??= await connections.ClientForAsync(connection, cancellationToken);

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await EwsOperations.WellKnownAsync(await ClientAsync(cancellationToken), ["inbox"], cancellationToken);

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(cancellationToken);
        var wellKnown = await EwsOperations.WellKnownAsync(client, [.. Roles.Select(r => r.WellKnown)], cancellationToken);
        var roles = Roles.Where(r => wellKnown.ContainsKey(r.WellKnown)).ToDictionary(r => wellKnown[r.WellKnown], r => r.Role, StringComparer.Ordinal);
        var (rootId, all) = await EwsOperations.FindFoldersAsync(client, cancellationToken);

        // Mail folders only (no class: created by other programs); a folder below a non-mail one is left out with it.
        var mail = all.Where(IsMailFolder).ToDictionary(EwsOperations.FolderIdOf, StringComparer.Ordinal);
        bool Reachable(XElement folder)
        {
            for (var parent = EwsOperations.ParentIdOf(folder); parent != rootId; parent = EwsOperations.ParentIdOf(mail[parent!]))
            {
                if (parent is null || !mail.ContainsKey(parent))
                {
                    return false;
                }
            }

            return true;
        }

        var result = mail.Values.Where(Reachable).Select(f =>
        {
            var id = EwsOperations.FolderIdOf(f);
            var parent = EwsOperations.ParentIdOf(f) is var p && p != rootId ? p : null;
            var role = roles.GetValueOrDefault(id);
            var name = f.Val("DisplayName") ?? id;
            if (role == FolderRole.None && parent is null && name is "Archive" or "Archiv")
            {
                role = FolderRole.Archive;
            }

            return new MailFolder(connection.Id, id, name, parent, role, f.Int("TotalCount"), f.Int("UnreadCount"));
        }).ToList();
        return result;
    }

    internal static bool IsMailFolder(XElement folder) =>
        folder.Name.LocalName == "Folder"
        && (folder.Val("FolderClass") is not { } folderClass || folderClass == "IPF.Note" || folderClass.StartsWith("IPF.Note.", StringComparison.Ordinal));

    public async Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);
        var client = await ClientAsync(cancellationToken);
        var known = knownRemoteIds.ToHashSet(StringComparer.Ordinal);
        if (folder.SyncState is { } state)
        {
            try
            {
                return await SyncChangesAsync(client, folder, state, known, cancellationToken);
            }
            catch (EwsException ex) when (ex.Code is "ErrorInvalidSyncStateData" or "ErrorInvalidSyncStateDataSize")
            {
                // The server forgot this state: start over.
            }
        }

        // First sync: remember where the folder is now, then take the newest messages.
        var (newState, _, _) = await ChangesAsync(client, folder.RemoteId, null, cancellationToken);
        var (newest, _) = await EwsOperations.FindItemIdsAsync(client, folder.RemoteId, cancellationToken, InitialMessages, newestFirst: true);
        var added = await SummariesAsync(client, newest.Select(i => i.Id), cancellationToken);
        var (total, unread) = await CountsAsync(client, folder.RemoteId, cancellationToken);
        var present = added.Select(a => a.RemoteId).ToHashSet(StringComparer.Ordinal);
        return new FolderSyncResult(newState, true, added, new Dictionary<string, MessageFlags>(), [.. known.Where(k => !present.Contains(k))], total, unread);
    }

    private static async Task<FolderSyncResult> SyncChangesAsync(EwsClient client, MailFolder folder, string state, HashSet<string> known, CancellationToken cancellationToken)
    {
        var (newState, changed, deleted) = await ChangesAsync(client, folder.RemoteId, state, cancellationToken);

        // New messages and changed known ones (read, flagged, answered): their current summary.
        var summaries = await SummariesAsync(client, changed.Where(c => c.Created || known.Contains(c.Id)).Select(c => c.Id).Distinct(StringComparer.Ordinal), cancellationToken);
        var added = summaries.Where(s => !known.Contains(s.RemoteId)).ToList();
        var flags = summaries.Where(s => known.Contains(s.RemoteId)).ToDictionary(s => s.RemoteId, s => s.Flags, StringComparer.Ordinal);
        var (total, unread) = await CountsAsync(client, folder.RemoteId, cancellationToken);
        return new FolderSyncResult(newState, false, added, flags, [.. deleted.Where(known.Contains)], total, unread);
    }

    /// <summary>All changes since <paramref name="state"/> (null: everything there is, to get a state), page by page.</summary>
    private static async Task<(string State, List<(string Id, bool Created)> Changed, List<string> Deleted)> ChangesAsync(
        EwsClient client, string folderId, string? state, CancellationToken cancellationToken)
    {
        var changed = new List<(string, bool)>();
        var deleted = new List<string>();
        while (true)
        {
            var response = (await client.CallAsync(X.Msg("SyncFolderItems",
                X.Msg("ItemShape", X.Typ("BaseShape", "IdOnly")),
                X.Msg("SyncFolderId", X.FolderId(folderId)),
                state is null ? null : X.Msg("SyncState", state),
                X.Msg("MaxChangesReturned", 512),
                X.Msg("SyncScope", "NormalItems")), cancellationToken)).Single();
            state = response.Element(X.M + "SyncState")?.Value ?? state ?? string.Empty;
            foreach (var change in response.Element(X.M + "Changes")?.Elements() ?? [])
            {
                var id = change.Name.LocalName is "Delete" or "ReadFlagChange"
                    ? change.Element(X.T + "ItemId")?.Attribute("Id")?.Value
                    : change.Elements().FirstOrDefault().Id();
                if (id is null)
                {
                    continue;
                }

                if (change.Name.LocalName == "Delete")
                {
                    deleted.Add(id);
                }
                else
                {
                    changed.Add((id, change.Name.LocalName == "Create"));
                }
            }

            if (response.Element(X.M + "IncludesLastItemInRange")?.Value != "false")
            {
                return (state, changed, deleted);
            }
        }
    }

    private static async Task<(int Total, int Unread)> CountsAsync(EwsClient client, string folderId, CancellationToken cancellationToken)
    {
        var folder = (await client.CallAsync(X.Msg("GetFolder",
            X.Msg("FolderShape", X.Typ("BaseShape", "IdOnly"), X.Typ("AdditionalProperties", X.Field("folder:TotalCount"), X.Field("folder:UnreadCount"))),
            X.Msg("FolderIds", X.FolderId(folderId))), cancellationToken)).Single().Element(X.M + "Folders")?.Elements().FirstOrDefault();
        return (folder.Int("TotalCount"), folder.Int("UnreadCount"));
    }

    private static readonly object[] SummaryFields =
    [
        "item:Subject", "item:DateTimeReceived", "item:HasAttachments", "item:IsDraft", "item:Flag", "item:Size", "item:Preview",
        "item:ItemClass", "item:InReplyTo", "message:From", "message:ToRecipients", "message:InternetMessageId", "message:IsRead",
        X.Extended(LastVerbTag, "Integer"),
    ];

    private static async Task<List<MessageSummary>> SummariesAsync(EwsClient client, IEnumerable<string> ids, CancellationToken cancellationToken) =>
        [.. (await EwsOperations.GetItemsAsync(client, ids, EwsOperations.Shape(SummaryFields), cancellationToken)).Select(SummaryOf)];

    internal static MessageSummary SummaryOf(XElement m)
    {
        var itemClass = m.Val("ItemClass") ?? string.Empty;
        var security = itemClass.StartsWith("IPM.Note.SMIME.MultipartSigned", StringComparison.OrdinalIgnoreCase) ? Core.Mail.MessageSecurity.Signed
            : itemClass.StartsWith("IPM.Note.SMIME", StringComparison.OrdinalIgnoreCase) ? Core.Mail.MessageSecurity.Encrypted
            : Core.Mail.MessageSecurity.None;
        return new MessageSummary(
            m.Id()!,
            m.Val("InternetMessageId"),
            m.Val("InReplyTo"),
            m.Val("Subject") ?? string.Empty,
            AddressOf(m.Get("From")?.Get("Mailbox")),
            [.. (m.Get("ToRecipients")?.Elements(X.T + "Mailbox") ?? []).Select(AddressOf).OfType<MailAddress>()],
            m.Date("DateTimeReceived") ?? DateTimeOffset.UnixEpoch,
            FlagsOf(m),
            long.TryParse(m.Val("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
            m.Bool("HasAttachments"),
            m.Val("Preview"),
            security);
    }

    internal static MessageFlags FlagsOf(XElement m) =>
        (m.Bool("IsRead") ? MessageFlags.Seen : MessageFlags.None)
        | (m.Bool("IsDraft") ? MessageFlags.Draft : MessageFlags.None)
        | (m.Get("Flag").Val("FlagStatus") == "Flagged" ? MessageFlags.Flagged : MessageFlags.None)
        | (m.ExtendedValue(LastVerbTag) switch { "102" or "103" => MessageFlags.Answered, "104" => MessageFlags.Forwarded, _ => MessageFlags.None });

    /// <summary>A mailbox as an address; internal ones without SMTP address (routing type EX) are left out.</summary>
    internal static MailAddress? AddressOf(XElement? mailbox) =>
        mailbox.Val("EmailAddress") is { } address && address.Contains('@', StringComparison.Ordinal) && mailbox.Val("RoutingType") is null or "SMTP"
            ? new MailAddress(mailbox.Val("Name"), address)
            : null;

    public async Task<MimeMessage> GetMessageAsync(MailFolder folder, string remoteId, CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(cancellationToken);
        var item = (await EwsOperations.GetItemsAsync(client, [remoteId], EwsOperations.Shape([], mime: true), cancellationToken)).FirstOrDefault()
                   ?? throw new EwsException("ErrorItemNotFound", Core.Localization.Texts.T("Die Nachricht ist nicht mehr auf dem Server."));
        using var stream = new MemoryStream(Convert.FromBase64String(item.Val("MimeContent") ?? string.Empty));
        return await MimeMessage.LoadAsync(stream, cancellationToken);
    }

    public async Task SetFlagsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        var updates = new List<XElement>();
        if (flags.HasFlag(MessageFlags.Seen))
        {
            updates.Add(Set(X.Field("message:IsRead"), X.Typ("IsRead", add ? "true" : "false")));
        }

        if (flags.HasFlag(MessageFlags.Flagged))
        {
            updates.Add(Set(X.Field("item:Flag"), X.Typ("Flag", X.Typ("FlagStatus", add ? "Flagged" : "NotFlagged"))));
        }

        // Answered/forwarded: the properties other mail programs show the arrow icons from.
        if (add && (flags.HasFlag(MessageFlags.Answered) || flags.HasFlag(MessageFlags.Forwarded)))
        {
            var forwarded = flags.HasFlag(MessageFlags.Forwarded);
            updates.Add(Set(X.Extended(LastVerbTag, "Integer"), Extended(LastVerbTag, "Integer", forwarded ? "104" : "102")));
            updates.Add(Set(X.Extended(IconTag, "Integer"), Extended(IconTag, "Integer", forwarded ? "262" : "261")));
        }

        if (updates.Count == 0 || remoteIds.Count == 0)
        {
            return;
        }

        var client = await ClientAsync(cancellationToken);
        await client.CallInBatchesAsync(remoteIds, 50, chunk => X.Msg("UpdateItem",
            new XAttribute("ConflictResolution", "AlwaysOverwrite"), new XAttribute("MessageDisposition", "SaveOnly"), new XAttribute("SuppressReadReceipts", "true"),
            X.Msg("ItemChanges", chunk.Select(id => X.Typ("ItemChange", X.ItemId(id), X.Typ("Updates", updates.Select(u => new XElement(u))))))),
            cancellationToken, "ErrorItemNotFound");
    }

    private static XElement Set(XElement field, XElement value) => X.Typ("SetItemField", field, X.Typ("Message", value));

    private static XElement Extended(int tag, string type, string value) => X.Typ("ExtendedProperty", X.Extended(tag, type), X.Typ("Value", value));

    public async Task MoveAsync(MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        ArgumentNullException.ThrowIfNull(target);
        var client = await ClientAsync(cancellationToken);
        await client.CallInBatchesAsync(remoteIds, 100, chunk => X.Msg("MoveItem",
            X.Msg("ToFolderId", X.FolderId(target.RemoteId)), X.Msg("ItemIds", chunk.Select(id => X.ItemId(id)))), cancellationToken, "ErrorItemNotFound");
    }

    public async Task DeleteAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        var client = await ClientAsync(cancellationToken);
        await client.CallInBatchesAsync(remoteIds, 100, chunk => X.Msg("DeleteItem", new XAttribute("DeleteType", "HardDelete"),
            X.Msg("ItemIds", chunk.Select(id => X.ItemId(id)))), cancellationToken, "ErrorItemNotFound");
    }

    public async Task MarkAllReadAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var client = await ClientAsync(cancellationToken);
        await client.CallAsync(X.Msg("MarkAllItemsAsRead", X.Msg("ReadFlag", "true"), X.Msg("SuppressReadReceipts", "true"),
            X.Msg("FolderIds", X.FolderId(folder.RemoteId))), cancellationToken);
    }

    public async Task EmptyAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var client = await ClientAsync(cancellationToken);
        await client.CallAsync(X.Msg("EmptyFolder", new XAttribute("DeleteType", "HardDelete"), new XAttribute("DeleteSubFolders", "false"),
            X.Msg("FolderIds", X.FolderId(folder.RemoteId))), cancellationToken);
    }

    /// <summary>Exchange sends the message and keeps a copy in "Gesendete Elemente".</summary>
    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var client = await ClientAsync(cancellationToken);
        var deliveryReceipt = Receipts.TakeDeliveryRequest(message);

        // Blind copies also as recipients of the item: a MIME header alone does not reliably reach them.
        var bcc = message.Bcc.Mailboxes.Select(b => X.Typ("Mailbox", X.Typ("Name", b.Name ?? string.Empty), X.Typ("EmailAddress", b.Address))).ToList();
        await client.CallAsync(X.Msg("CreateItem", new XAttribute("MessageDisposition", "SendAndSaveCopy"),
            X.Msg("SavedItemFolderId", X.FolderId("sentitems")),
            X.Msg("Items", X.Typ("Message",
                X.Typ("MimeContent", new XAttribute("CharacterSet", "UTF-8"), await MimeBase64Async(message, cancellationToken)),
                bcc.Count > 0 ? X.Typ("BccRecipients", bcc) : null,
                Receipts.WantsReadReceipt(message) ? X.Typ("IsReadReceiptRequested", "true") : null,
                deliveryReceipt ? X.Typ("IsDeliveryReceiptRequested", "true") : null))), cancellationToken);
    }

    /// <summary>Stores the message as it is: read or not, a draft only if it is one, with its received date.</summary>
    public async Task AppendAsync(MailFolder folder, MimeMessage message, MessageFlags flags, DateTimeOffset? receivedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(message);
        var client = await ClientAsync(cancellationToken);
        var mapiFlags = (flags.HasFlag(MessageFlags.Seen) ? 1 : 0) | (flags.HasFlag(MessageFlags.Draft) ? 8 : 0);
        await client.CallAsync(X.Msg("CreateItem", new XAttribute("MessageDisposition", "SaveOnly"),
            X.Msg("SavedItemFolderId", X.FolderId(folder.RemoteId)),
            X.Msg("Items", X.Typ("Message",
                X.Typ("MimeContent", new XAttribute("CharacterSet", "UTF-8"), await MimeBase64Async(message, cancellationToken)),
                Extended(MessageFlagsTag, "Integer", mapiFlags.ToString(CultureInfo.InvariantCulture)),
                receivedAt is { } at ? Extended(DeliveryTimeTag, "SystemTime", X.Utc(at)) : null,
                X.Typ("IsRead", flags.HasFlag(MessageFlags.Seen) ? "true" : "false")))), cancellationToken);
    }

    public async Task<IReadOnlyList<MessageSummary>> FetchOlderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);
        var client = await ClientAsync(cancellationToken);

        // Known are the newest ones: the next older ones follow them by date.
        var known = knownRemoteIds.ToHashSet(StringComparer.Ordinal);
        var (older, _) = await EwsOperations.FindItemIdsAsync(client, folder.RemoteId, cancellationToken, count, known.Count, newestFirst: true);
        return await SummariesAsync(client, older.Select(o => o.Id).Where(id => !known.Contains(id)), cancellationToken);
    }

    public async Task<(IReadOnlyList<MessageSummary> Hits, bool IsTruncated)> SearchAsync(MailFolder folder, MailSearchQuery query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(query);
        var client = await ClientAsync(cancellationToken);
        var (hits, more) = await EwsOperations.FindItemIdsAsync(client, folder.RemoteId, cancellationToken, limit, newestFirst: true, query: SearchText(query));
        return (await SummariesAsync(client, hits.Select(h => h.Id), cancellationToken), more);
    }

    /// <summary>The query in Exchange's search syntax (AQS): all given parts must match.</summary>
    internal static string SearchText(MailSearchQuery query)
    {
        static string Quote(string value) => "\"" + value.Trim().Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.From))
        {
            parts.Add("from:" + Quote(query.From));
        }

        if (!string.IsNullOrWhiteSpace(query.Subject))
        {
            parts.Add("subject:" + Quote(query.Subject));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            parts.Add("body:" + Quote(query.Text));
        }

        if (!string.IsNullOrWhiteSpace(query.Anywhere))
        {
            parts.Add(Quote(query.Anywhere));
        }

        return string.Join(" AND ", parts);
    }

    /// <summary>No push yet (EWS streaming notifications are for later): wait, then sync again.</summary>
    public Task WaitForChangesAsync(MailFolder folder, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);

    public async Task<MailFolder> CreateFolderAsync(string name, FolderRole role, CancellationToken cancellationToken = default)
    {
        var id = await CreateAsync(name, "msgfolderroot", cancellationToken);
        return new MailFolder(connection.Id, id, name, null, role);
    }

    public async Task<MailFolder> CreateSubfolderAsync(string name, MailFolder? parent, CancellationToken cancellationToken = default)
    {
        var id = await CreateAsync(name, parent?.RemoteId ?? "msgfolderroot", cancellationToken);
        return new MailFolder(connection.Id, id, name, parent?.RemoteId, FolderRole.None);
    }

    private async Task<string> CreateAsync(string name, string parentId, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        var response = (await client.CallAsync(X.Msg("CreateFolder",
            X.Msg("ParentFolderId", X.FolderId(parentId)),
            X.Msg("Folders", X.Typ("Folder", X.Typ("FolderClass", "IPF.Note"), X.Typ("DisplayName", name)))), cancellationToken)).Single();
        return response.Descendants(X.T + "FolderId").First().Attribute("Id")!.Value;
    }

    public async Task RenameFolderAsync(MailFolder folder, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var client = await ClientAsync(cancellationToken);
        await client.CallAsync(X.Msg("UpdateFolder", X.Msg("FolderChanges", X.Typ("FolderChange",
            X.Typ("FolderId", new XAttribute("Id", folder.RemoteId)),
            X.Typ("Updates", X.Typ("SetFolderField", X.Field("folder:DisplayName"), X.Typ("Folder", X.Typ("DisplayName", name))))))), cancellationToken);
    }

    public async Task DeleteFolderAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var client = await ClientAsync(cancellationToken);
        await client.CallAsync(X.Msg("DeleteFolder", new XAttribute("DeleteType", "HardDelete"),
            X.Msg("FolderIds", X.Typ("FolderId", new XAttribute("Id", folder.RemoteId)))), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAllRemoteIdsAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var (all, _) = await EwsOperations.FindItemIdsAsync(await ClientAsync(cancellationToken), folder.RemoteId, cancellationToken);
        return [.. all.Select(a => a.Id)];
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> MimeBase64Async(MimeMessage message, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        return Convert.ToBase64String(stream.ToArray());
    }
}
