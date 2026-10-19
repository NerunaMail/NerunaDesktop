using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Neruna.Core.Mail;
using XstReader;
using XstReader.ElementProperties;
using MessageFlags = Neruna.Core.Mail.MessageFlags;

namespace Neruna.Providers.Pst;

internal sealed record PstFolderInfo(string RemoteId, string Name, string? ParentRemoteId, FolderRole Role, int Total, int Unread);

/// <summary>
/// One open data file (.pst/.ost), read with XstReader. The file is never written. Ids are positions in the file
/// (folders: "2/0", messages: "17.ab12cd34" with a fingerprint) – stable while the file stays the same; a changed file
/// gets a new <see cref="Stamp"/> and everything is read again.
/// </summary>
internal sealed class PstArchive : IDisposable
{
    // One open file per path: XstReader keeps what it read, opening again for every message would read it again.
    private static readonly ConcurrentDictionary<string, PstArchive> Open = new(StringComparer.Ordinal);

    private readonly XstFile _file;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private XstFolder? _top;

    // XstReader hands out new folder objects on every walk through the tree; kept here, so a folder's contents table
    // (cached per folder object, see All) is read once – not once per message, which made a large folder quadratic.
    private readonly Dictionary<string, XstFolder> _folders = new(StringComparer.Ordinal);

    private PstArchive(string path, string stamp)
    {
        Path = path;
        Stamp = stamp;
        _file = new XstFile(path);
    }

    public string Path { get; }

    /// <summary>Size and time of the file: the sync state – unchanged file, nothing to do.</summary>
    public string Stamp { get; }

    /// <exception cref="PstException">Missing or not a data file.</exception>
    public static PstArchive For(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new PstException(Neruna.Core.Localization.Texts.F("Die Datei «{0}» wurde nicht gefunden.", path));
        }

        var stamp = string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        while (true)
        {
            if (Open.TryGetValue(path, out var open) && open.Stamp == stamp)
            {
                return open;
            }

            var created = new PstArchive(path, stamp);
            if (open is null ? Open.TryAdd(path, created) : Open.TryUpdate(path, created, open))
            {
                open?.Dispose();
                return created;
            }

            created.Dispose();
        }
    }

    /// <summary>Closes the file (the account was removed).</summary>
    public static void Close(string path)
    {
        if (Open.TryRemove(path, out var open))
        {
            open.Dispose();
        }
    }

    public async Task<T> ReadAsync<T>(Func<PstArchive, T> read, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    return read(this);
                }
                catch (Exception ex) when (ex is not PstException and not OperationCanceledException and not OutOfMemoryException)
                {
                    throw new PstException(Neruna.Core.Localization.Texts.T("Die Outlook-Datendatei lässt sich nicht lesen: ") + ex.Message, ex);
                }
            }, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Folders ──

    public IReadOnlyList<PstFolderInfo> Folders()
    {
        var result = new List<PstFolderInfo>();
        void Add(XstFolder folder, string? parentId)
        {
            var index = 0;
            foreach (var child in folder.Folders)
            {
                var id = parentId is null ? index.ToString(CultureInfo.InvariantCulture) : $"{parentId}/{index}";
                index++;
                if (!IsMailFolder(child))
                {
                    continue;
                }

                result.Add(new PstFolderInfo(id, child.DisplayName, parentId, RoleOf(child.DisplayName, parentId), child.ContentCount, child.ContentUnreadCount));
                Add(child, id);
            }
        }

        Add(Top(), null);
        return result;
    }

    public XstFolder Folder(string remoteId)
    {
        if (_folders.TryGetValue(remoteId, out var known))
        {
            return known;
        }

        var folder = Top();
        foreach (var part in remoteId.Split('/'))
        {
            folder = int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && folder.Folders.ElementAtOrDefault(index) is { } child
                ? child
                : throw new PstException(Neruna.Core.Localization.Texts.T("Der Ordner ist in der Outlook-Datendatei nicht mehr vorhanden."));
        }

        _folders[remoteId] = folder;
        return folder;
    }

    /// <summary>
    /// The folders a person sees in Outlook ("Top of Personal Folders" / "IPM_SUBTREE"), not the store's own ones
    /// (views, search, free/busy). Their names differ by Outlook language, so the top is told by what it holds.
    /// </summary>
    private XstFolder Top() => _top ??= FindTop(_file.RootFolder);

    private static XstFolder FindTop(XstFolder root)
    {
        // The top's own children and their children are both candidates (a PST has the top below the root, an OST
        // one level deeper) – so a person's own folder like "Ferien" is one too.
        var candidates = root.Folders.Concat(root.Folders.SelectMany(f => f.Folders)).ToList();
        var best = BestTop([.. candidates.Select(f => (IReadOnlyList<string>)[.. f.Folders.Where(IsMailFolder).Select(c => c.DisplayName)])]);
        return best < 0 ? root : candidates[best];
    }

    /// <summary>
    /// Of the candidates (the names of their mail folders), the one that is the top: the one holding Outlook's own
    /// folders (inbox, sent, deleted …) – a person's folder with many subfolders must not win over it; between
    /// equals, the one with more mail folders. -1: none has mail folders.
    /// </summary>
    internal static int BestTop(IReadOnlyList<IReadOnlyList<string>> candidates) =>
        candidates
            .Select((names, index) => (Index: index, Roles: names.Select(n => RoleOf(n, null)).Where(r => r != FolderRole.None).Distinct().Count(), names.Count))
            .Where(c => c.Count > 0)
            .OrderByDescending(c => c.Roles)
            .ThenByDescending(c => c.Count)
            .Select(c => c.Index)
            .DefaultIfEmpty(-1)
            .First();

    // Calendar, contacts, tasks, notes, journal and Outlook's own folders hold no mail; neither do "Sync Issues"
    // (an OST's conflict logs, often thousands of them, with their subfolders).
    private static bool IsMailFolder(XstFolder folder)
    {
        if (IsSyncIssues(folder.DisplayName))
        {
            return false;
        }

        var kind = folder.PropertyValue(PropertyCanonicalName.PidTagContainerClass) as string;
        return string.IsNullOrEmpty(kind)
            ? !folder.DisplayName.StartsWith('~') && folder.DisplayName is not ("Freebusy Data" or "IPM_COMMON_VIEWS" or "IPM_VIEWS" or "Search Root" or "Finder" or "Views" or "Common Views" or "Shortcuts" or "Shared Data" or "Drizzle")
            : kind.StartsWith("IPF.Note", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("IPF.Imap", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSyncIssues(string name) =>
        name.StartsWith("Sync Issues", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Synchronisierungsprobleme", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Problèmes de synchronisation", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Problemi di sincronizzazione", StringComparison.OrdinalIgnoreCase);

    // Outlook's folders by their name (Outlook's own and, in an OST of an IMAP account, the server's); drafts stay
    // ordinary folders here – nothing is written.
    internal static FolderRole RoleOf(string name, string? parentId) => parentId is not null ? FolderRole.None : name.ToLowerInvariant() switch
    {
        "inbox" or "posteingang" or "boîte de réception" or "posta in arrivo" => FolderRole.Inbox,
        "sent items" or "gesendete elemente" or "gesendete objekte" or "éléments envoyés" or "posta inviata"
            or "sent" or "sent messages" or "sent mail" or "gesendet" or "gesendete nachrichten" or "envoyés" or "inviata" => FolderRole.Sent,
        "deleted items" or "gelöschte elemente" or "gelöschte objekte" or "éléments supprimés" or "posta eliminata"
            or "trash" or "deleted messages" or "papierkorb" or "gelöschte nachrichten" or "corbeille" or "cestino" => FolderRole.Trash,
        "junk e-mail" or "junk-e-mail" or "courrier indésirable" or "posta indesiderata" or "junk" or "spam" => FolderRole.Junk,
        "archive" or "archiv" or "archivio" => FolderRole.Archive,
        _ => FolderRole.None,
    };

    // ── Messages ──

    /// <summary>The mail of a folder (not appointments or contacts filed there), with its position.</summary>
    public static IEnumerable<(int Index, XstMessage Message)> Messages(XstFolder folder) =>
        All(folder).Select((m, i) => (Index: i, Message: m)).Where(m => IsMail(m.Message) || Forget(m.Message));

    // Appointments and contacts in a mail folder: what was read to tell is not kept.
    private static bool Forget(XstMessage message)
    {
        message.ClearContents();
        return false;
    }

    // The folder's contents table – read here, not with XstFolder.Messages: that one also tries to decrypt S/MIME mail
    // itself (with the certificates of Windows) and on the first it cannot, ends the whole list ("ASN1 bad tag value").
    // Neruna opens S/MIME itself (see Item). Internal API of XstReader.Api 1.0.6 (pinned); another version falls back.
    // Kept for the last few folders only: a 20 GB file has hundreds of thousands of messages.
    private const int KeptContents = 3;
    private static readonly ConditionalWeakTable<XstFolder, List<XstMessage>> Contents = [];
    private static readonly LinkedList<XstFolder> RecentContents = [];

    private static List<XstMessage> All(XstFolder folder)
    {
        lock (RecentContents)
        {
            if (Contents.TryGetValue(folder, out var cached))
            {
                return cached;
            }
        }

        var messages = folder.ContentCount == 0 ? [] : ReadContents(folder) ?? [.. folder.Messages];
        lock (RecentContents)
        {
            Contents.AddOrUpdate(folder, messages);
            RecentContents.AddFirst(folder);
            while (RecentContents.Count > KeptContents)
            {
                Contents.Remove(RecentContents.Last!.Value);
                RecentContents.RemoveLast();
            }
        }

        return messages;
    }

    private static List<XstMessage>? ReadContents(XstFolder folder)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var assembly = typeof(XstFolder).Assembly;
        var nidType = assembly.GetType("XstReader.NID");
        var enidType = assembly.GetType("XstReader.EnidType");
        var ltp = typeof(XstElement).GetProperty("Ltp", Any)?.GetValue(folder);
        var nid = typeof(XstElement).GetProperty("Nid", Any)?.GetValue(folder);
        var typed = nidType?.GetMethod("TypedNID", Any);
        var initialize = typeof(XstMessage).GetMethod("Initialize", Any, [nidType!, typeof(XstFolder)]);
        var readTable = ltp?.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "ReadTable" && m.GetParameters().Length == 3)?.MakeGenericMethod(typeof(XstMessage));
        if (nidType is null || enidType is null || ltp is null || nid is null || typed is null || initialize is null || readTable is null)
        {
            return null;
        }

        var contents = typed.Invoke(null, [Enum.Parse(enidType, "CONTENTS_TABLE"), nid]);
        Action<XstMessage, uint> create = (message, id) => initialize.Invoke(message, [Activator.CreateInstance(nidType, id), folder]);
        Action<XstMessage> nothing = _ => { };
        try
        {
            return [.. (IEnumerable<XstMessage>)readTable.Invoke(ltp, [contents, create, nothing])!];
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            throw inner;
        }
    }

    /// <summary>
    /// Reads from a message and lets XstReader forget what it loaded for it (properties, body, attachments): it keeps
    /// all of that otherwise, and the search index reading every message of a 20 GB file would fill the memory.
    /// </summary>
    public static T Read<T>(XstMessage message, Func<XstMessage, T> read)
    {
        try
        {
            return read(message);
        }
        finally
        {
            message.ClearContents();
        }
    }

    public static string RemoteIdOf(int index, XstMessage message) => $"{index.ToString(CultureInfo.InvariantCulture)}.{Fingerprint(message)}";

    /// <exception cref="PstException">The message is not (or no longer) in the folder.</exception>
    public static XstMessage Message(XstFolder folder, string remoteId)
    {
        var dot = remoteId.IndexOf('.', StringComparison.Ordinal);
        var all = All(folder);
        if (dot > 0 && int.TryParse(remoteId.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index < all.Count && RemoteIdOf(index, all[index]) == remoteId)
        {
            return all[index];
        }

        // The file was replaced by a newer copy: found by its fingerprint where it is now.
        var print = dot > 0 ? remoteId[(dot + 1)..] : remoteId;
        return all.FirstOrDefault(m => Read(m, Fingerprint) == print)
               ?? throw new PstException(Neruna.Core.Localization.Texts.T("Die Nachricht ist in der Outlook-Datendatei nicht mehr vorhanden."));
    }

    private static bool IsMail(XstMessage message)
    {
        var kind = message.PropertyValue(PropertyCanonicalName.PidTagMessageClass) as string;
        return string.IsNullOrEmpty(kind)
               || kind.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase)
               || kind.StartsWith("IPM.Schedule.Meeting", StringComparison.OrdinalIgnoreCase)
               || kind.StartsWith("IPM.Post", StringComparison.OrdinalIgnoreCase)
               || kind.StartsWith("REPORT.", StringComparison.OrdinalIgnoreCase);
    }

    private static string Fingerprint(XstMessage message)
    {
        var key = string.Join('\n', message.InternetMessageId, message.SubmittedTime?.Ticks, message.ReceivedTime?.Ticks, message.Subject);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 6));
    }

    /// <summary>
    /// The message in the list. <paramref name="full"/> false: only what the folder's contents table already brought
    /// (subject, sender, date, read, attachments, size) – asking for anything else makes XstReader load the message's
    /// whole property block, some 20 ms each in an OST. Flagged and answered then come with a second pass (<see cref="Flags"/>).
    /// </summary>
    public static MessageSummary Summary(int index, XstMessage message, bool full = true)
    {
        var flags = message.IsRead ? MessageFlags.Seen : MessageFlags.None;
        if (full || Has(message, PropertyCanonicalName.PidTagFlagStatus) || Has(message, PropertyCanonicalName.PidTagLastVerbExecuted))
        {
            flags |= OutlookFlags(message);
        }

        // Sender and "To" from the message's own properties: reading the recipient table costs ~70 ms a message in an
        // OST and is not needed for the list (the message read in full still uses it).
        var item = ListAddresses(message, full);
        var date = Utc(message.ReceivedTime ?? message.SubmittedTime ?? message.Date) ?? DateTimeOffset.UnixEpoch;
        return new MessageSummary(
            RemoteIdOf(index, message),
            Id(message.InternetMessageId),
            full || Has(message, PropertyCanonicalName.PidTagInReplyToId) ? Id(message.PropertyValue(PropertyCanonicalName.PidTagInReplyToId) as string) : null,
            message.Subject ?? string.Empty,
            item.From is { } from ? new MailAddress(from.Name, from.Address is { } a && a.Contains('@', StringComparison.Ordinal) ? a : string.Empty) : null,
            [.. item.To.Select(t => new MailAddress(t.Name, t.Address is { } a && a.Contains('@', StringComparison.Ordinal) ? a : string.Empty))],
            date,
            flags,
            message.PropertyValue(PropertyCanonicalName.PidTagMessageSize) is int size ? size : 0,
            HasAttachments(message) && SmimeKind(message) is null,
            null,
            SmimeKind(message) switch
            {
                true => MessageSecurity.Signed,
                false => MessageSecurity.Encrypted,
                null => MessageSecurity.None,
            });
    }

    /// <summary>Everything that goes into the MIME message, attached messages included.</summary>
    public static PstItem Item(XstMessage message, int depth = 0)
    {
        var (from, to, cc, bcc) = Recipients(message);

        // S/MIME: Outlook keeps it as one attachment ("smime.p7m"); Neruna opens it itself.
        if (SmimeKind(message) is { } multipartSigned && message.Attachments.FirstOrDefault() is { } secured)
        {
            using var data = new MemoryStream();
            secured.SaveToStream(data);
            return new PstItem(Headers(message), message.Subject, from, to, cc, bcc, Utc(message.SubmittedTime ?? message.Date ?? message.ReceivedTime),
                message.InternetMessageId, message.PropertyValue(PropertyCanonicalName.PidTagInReplyToId) as string, null, null, null, [])
            {
                Smime = data.ToArray(),
                SmimeIsMime = multipartSigned,
            };
        }

        string? html = null, text = null, rtf = null;
        var body = message.Body;
        switch (body?.Format)
        {
            case XstMessageBodyFormat.Html:
                html = body.Text;
                break;
            case XstMessageBodyFormat.Rtf:
                rtf = body.Text;
                text = message.PropertyValue(PropertyCanonicalName.PidTagBody) as string;
                break;
            case XstMessageBodyFormat.PlainText:
                text = body.Text;
                break;
        }

        var attachments = new List<PstAttachment>();
        foreach (var attachment in message.Attachments)
        {
            if (attachment.IsEmail && depth < 5 && attachment.AttachedEmailMessage is { } attached)
            {
                attachments.Add(new PstAttachment((attached.Subject ?? "Nachricht") + ".eml", [], Message: Item(attached, depth + 1)));
            }
            else if (attachment.IsFile)
            {
                using var content = new MemoryStream();
                attachment.SaveToStream(content);
                var name = attachment.LongFileName is { Length: > 0 } longName ? longName
                    : attachment.FileName is { Length: > 0 } shortName ? shortName
                    : attachment.DisplayName ?? "Anhang";
                attachments.Add(new PstAttachment(name, content.ToArray(), attachment.HasContentId ? attachment.ContentId : null, attachment.IsInlineAttachment));
            }
        }

        return new PstItem(
            Headers(message),
            message.Subject,
            from,
            to,
            cc,
            bcc,
            Utc(message.SubmittedTime ?? message.Date ?? message.ReceivedTime),
            message.InternetMessageId,
            message.PropertyValue(PropertyCanonicalName.PidTagInReplyToId) as string,
            html,
            text,
            rtf,
            attachments);
    }

    private static string? Headers(XstMessage message) => message.PropertyValue(PropertyCanonicalName.PidTagTransportMessageHeaders) as string;

    /// <summary>Null: no S/MIME; true: signed as multipart/signed (clear text); false: encrypted or opaquely signed.</summary>
    private static bool? SmimeKind(XstMessage message) =>
        message.PropertyValue(PropertyCanonicalName.PidTagMessageClass) is string kind && kind.StartsWith("IPM.Note.SMIME", StringComparison.OrdinalIgnoreCase)
            ? kind.Contains("MultipartSigned", StringComparison.OrdinalIgnoreCase)
            : null;

    /// <summary>From the message flags (MSGFLAG_HASATTACH) – XstReader's HasAttachments reads the attachment table.</summary>
    private static bool HasAttachments(XstMessage message) =>
        message.PropertyValue(PropertyCanonicalName.PidTagMessageFlags) is int flags ? (flags & 0x10) != 0 : message.HasAttachments;

    /// <summary>The flags Outlook set (flagged, answered, forwarded), for the second pass over a folder.</summary>
    public static MessageFlags Flags(XstMessage message) => (message.IsRead ? MessageFlags.Seen : MessageFlags.None) | OutlookFlags(message);

    /// <summary>Flagged, answered, forwarded as Outlook set them.</summary>
    public static MessageFlags OutlookFlags(XstMessage message)
    {
        var flags = message.PropertyValue(PropertyCanonicalName.PidTagFlagStatus) is int status && status == 2 ? MessageFlags.Flagged : MessageFlags.None;
        return flags | message.PropertyValue(PropertyCanonicalName.PidTagLastVerbExecuted) switch
        {
            102 or 103 => MessageFlags.Answered,
            104 => MessageFlags.Forwarded,
            _ => MessageFlags.None,
        };
    }

    private static readonly Func<XstPropertySet, System.Collections.IDictionary?> LoadedProperties = CreateLoadedProperties();

    // XstReader keeps the properties it has so far (from the contents table, or loaded) in a private dictionary.
    private static Func<XstPropertySet, System.Collections.IDictionary?> CreateLoadedProperties()
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var property = typeof(XstPropertySet).GetProperty("DicProperties", Any);
        var field = typeof(XstPropertySet).GetField("_DicProperties", Any);
        return set => (property?.GetValue(set) ?? field?.GetValue(set)) as System.Collections.IDictionary;
    }

    /// <summary>Whether XstReader has the property already, so asking for it costs nothing (unknown: assume yes).</summary>
    private static bool Has(XstMessage message, PropertyCanonicalName tag) => LoadedProperties(message.Properties) is not { } loaded || loaded.Contains(tag);

    /// <summary>
    /// For the list: the sender and the "To" names. The sender from its one-off entry id (name and SMTP address, in the
    /// contents table) – XstReader reads the name column of that table wrongly; without one (an Exchange sender) from
    /// the recipient table, which costs more but is then the only reliable source.
    /// </summary>
    private static (PstAddress? From, List<PstAddress> To) ListAddresses(XstMessage message, bool full)
    {
        string? Text(PropertyCanonicalName name) => (full || Has(message, name)) && message.PropertyValue(name) is string s && s.Trim().Length > 0 ? s.Trim() : null;
        var from = message.PropertyValue(PropertyCanonicalName.PidTagSentRepresentingEntryId) is byte[] entry && OneOff(entry) is { } oneOff
            ? new PstAddress(oneOff.Name.Length > 0 ? oneOff.Name : oneOff.Address, oneOff.Address)
            : Recipients(message).From;
        var to = (Text(PropertyCanonicalName.PidTagDisplayTo) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => new PstAddress(n, n.Contains('@', StringComparison.Ordinal) ? n : null))
            .ToList();
        return (from, to);
    }

    private static readonly byte[] OneOffProvider = Convert.FromHexString("812B1FA4BEA310199D6E00DD010F5402");

    /// <summary>
    /// Name and address in a one-off entry id (MS-OXCDATA 2.2.5.1: provider, flags, then display name, address type and
    /// address, Unicode or 8-bit); null for other kinds (an Exchange address book entry has no SMTP address in it).
    /// </summary>
    internal static (string Name, string Address)? OneOff(byte[] entry)
    {
        if (entry.Length < 24 || !entry.AsSpan(4, 16).SequenceEqual(OneOffProvider))
        {
            return null;
        }

        var unicode = (BitConverter.ToUInt16(entry, 22) & 0x8000) != 0;
        var parts = new List<string>();
        var position = 24;
        while (parts.Count < 3 && position < entry.Length)
        {
            var end = position;
            if (unicode)
            {
                while (end + 1 < entry.Length && (entry[end] != 0 || entry[end + 1] != 0))
                {
                    end += 2;
                }

                parts.Add(Encoding.Unicode.GetString(entry, position, end - position));
                position = end + 2;
            }
            else
            {
                while (end < entry.Length && entry[end] != 0)
                {
                    end++;
                }

                parts.Add(Encoding.Latin1.GetString(entry, position, end - position));
                position = end + 1;
            }
        }

        return parts.Count == 3 && parts[1].Equals("SMTP", StringComparison.OrdinalIgnoreCase) && parts[2].Contains('@', StringComparison.Ordinal)
            ? (parts[0].Trim(), parts[2].Trim())
            : null;
    }

    private static (PstAddress? From, List<PstAddress> To, List<PstAddress> Cc, List<PstAddress> Bcc) Recipients(XstMessage message)
    {
        PstAddress? sentBy = null, sender = null;
        List<PstAddress> to = [], cc = [], bcc = [];
        foreach (var recipient in message.Recipients.Items)
        {
            var address = new PstAddress(recipient.DisplayName, recipient.Address);
            switch (recipient.RecipientType)
            {
                case RecipientType.To:
                    to.Add(address);
                    break;
                case RecipientType.Cc:
                    cc.Add(address);
                    break;
                case RecipientType.Bcc:
                    bcc.Add(address);
                    break;
                case RecipientType.SentRepresenting:
                    sentBy = address;
                    break;
                case RecipientType.Sender or RecipientType.Originator:
                    sender ??= address;
                    break;
            }
        }

        sentBy ??= sender;

        // An Exchange sender ("/O=…") still has an SMTP address of its own.
        if (sentBy is { Address: var fromAddress } && fromAddress?.Contains('@', StringComparison.Ordinal) != true
            && (message.PropertyValue(PropertyCanonicalName.PidTagSentRepresentingSmtpAddress) ?? message.PropertyValue(PropertyCanonicalName.PidTagSenderSmtpAddress)) is string smtp)
        {
            sentBy = sentBy with { Address = smtp };
        }

        return (sentBy is { Name: "Unknown", Address: "Unknown" } ? null : sentBy, to, cc, bcc);
    }

    // XstReader gives local times (DateTimeKind.Local or Unspecified from UTC properties).
    private static DateTimeOffset? Utc(DateTime? time) => time is { } t
        ? new DateTimeOffset(t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc))
        : null;

    private static string? Id(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim().Trim('<', '>');

    public void Dispose()
    {
        _file.Dispose();
        _lock.Dispose();
    }
}

/// <summary>A data file that cannot be read (missing, damaged or no Outlook file); the message is shown as it is.</summary>
public sealed class PstException : Exception
{
    public PstException(string message)
        : base(message)
    {
    }

    public PstException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
