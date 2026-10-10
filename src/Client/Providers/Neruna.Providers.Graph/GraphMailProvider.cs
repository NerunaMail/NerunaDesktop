using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MimeKit;
using Neruna.Core.Mail;

namespace Neruna.Providers.Graph;

/// <summary>
/// Mail through Microsoft Graph. Folder ids are Graph ids; the sync state of a folder is its delta link. The first sync
/// fetches the last <see cref="InitialDays"/> days (older mail comes with "load more"); messages travel as MIME.
/// Graph files sent mail in "Gesendete Elemente" itself. No server push for desktop apps: changes are polled.
/// </summary>
internal sealed class GraphMailProvider(Guid connectionId, GraphClient graph, ILogger<GraphMailProvider> logger) : IMailProvider, IAutoReplyProvider
{
    public Task<(AutoReply Reply, AutoReplyFeatures Features)> GetAutoReplyAsync(CancellationToken cancellationToken = default) =>
        GraphAutoReply.GetAsync(graph, cancellationToken);

    public Task SetAutoReplyAsync(AutoReply reply, IReadOnlyList<string> ownAddresses, CancellationToken cancellationToken = default) =>
        GraphAutoReply.SetAsync(graph, reply, cancellationToken);

    public const int InitialDays = 90;

    // The list fields Neruna shows (the body comes as MIME when a message is opened).
    private const string SummaryFields = "id,internetMessageId,subject,from,toRecipients,receivedDateTime,isRead,isDraft,flag,hasAttachments,bodyPreview";

    // PR_LAST_VERB_EXECUTED: how the message was last answered (102 reply, 103 reply all, 104 forward).
    private const string LastVerbProperty = "Integer 0x1081";

    private static readonly (string WellKnown, FolderRole Role)[] Roles =
    [
        ("inbox", FolderRole.Inbox), ("sentitems", FolderRole.Sent), ("drafts", FolderRole.Drafts),
        ("deleteditems", FolderRole.Trash), ("junkemail", FolderRole.Junk), ("archive", FolderRole.Archive),
    ];

    public MailProviderCapabilities Capabilities =>
        MailProviderCapabilities.Send | MailProviderCapabilities.Flags | MailProviderCapabilities.Move | MailProviderCapabilities.Delete |
        MailProviderCapabilities.ServerSearch | MailProviderCapabilities.Append;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await graph.GetAsync("me/mailFolders/inbox?$select=id", cancellationToken);

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        var roles = new Dictionary<string, FolderRole>(StringComparer.Ordinal);
        foreach (var (wellKnown, role) in Roles)
        {
            try
            {
                var id = (await graph.GetAsync($"me/mailFolders/{wellKnown}?$select=id", cancellationToken)).Str("id");
                if (id is not null)
                {
                    roles[id] = role;
                }
            }
            catch (GraphException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                // e.g. no archive folder yet
            }
        }

        var result = new List<MailFolder>();
        async Task AddAsync(string path, string? parent)
        {
            foreach (var folder in await graph.GetAllAsync(path, cancellationToken))
            {
                var id = folder.Str("id")!;
                result.Add(new MailFolder(connectionId, id, folder.Str("displayName") ?? id, parent, roles.GetValueOrDefault(id),
                    folder.Int("totalItemCount"), folder.Int("unreadItemCount")));
                if (folder.Int("childFolderCount") > 0)
                {
                    await AddAsync($"me/mailFolders/{id}/childFolders?$top=100", id);
                }
            }
        }

        await AddAsync("me/mailFolders?$top=100", null);
        return result;
    }

    public async Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);
        var isFull = folder.SyncState is null;
        string? deltaLink = null;
        List<JsonElement> items;
        try
        {
            items = await graph.GetAllAsync(folder.SyncState ?? InitialDelta(folder.RemoteId, true), cancellationToken, d => deltaLink = d,
                headers: ("Prefer", "odata.maxpagesize=100"));
        }
        catch (GraphException ex) when (folder.SyncState is not null && (ex.Status == HttpStatusCode.Gone || ex.Code is "SyncStateNotFound" or "SyncStateInvalid"))
        {
            // The server forgot this sync state: start over.
            isFull = true;
            items = await graph.GetAllAsync(InitialDelta(folder.RemoteId, true), cancellationToken, d => deltaLink = d, headers: ("Prefer", "odata.maxpagesize=100"));
        }
        catch (GraphException ex) when (folder.SyncState is null && ex.Status == HttpStatusCode.BadRequest)
        {
            // A mailbox that does not take the date filter on delta: everything.
            logger.LogInformation(ex, "Delta with date filter refused; syncing the whole folder");
            items = await graph.GetAllAsync(InitialDelta(folder.RemoteId, false), cancellationToken, d => deltaLink = d, headers: ("Prefer", "odata.maxpagesize=100"));
        }

        var known = knownRemoteIds.ToHashSet(StringComparer.Ordinal);
        var added = new List<MessageSummary>();
        var flags = new Dictionary<string, MessageFlags>(StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var item in items)
        {
            var id = item.Str("id")!;
            if (item.TryGetProperty("@removed", out _))
            {
                removed.Add(id);
            }
            else if (known.Contains(id) && !isFull)
            {
                flags[id] = FlagsOf(item);
            }
            else
            {
                added.Add(SummaryOf(item));
            }
        }

        if (isFull)
        {
            removed.AddRange(known.Where(k => added.All(a => a.RemoteId != k)));
        }

        var counts = await graph.GetAsync($"me/mailFolders/{folder.RemoteId}?$select=totalItemCount,unreadItemCount", cancellationToken);
        return new FolderSyncResult(deltaLink ?? folder.SyncState ?? string.Empty, isFull, added, flags, removed, counts.Int("totalItemCount"), counts.Int("unreadItemCount"));
    }

    public async Task<MimeMessage> GetMessageAsync(MailFolder folder, string remoteId, CancellationToken cancellationToken = default)
    {
        var bytes = await graph.GetBytesAsync($"me/messages/{remoteId}/$value", cancellationToken);
        using var stream = new MemoryStream(bytes);
        return await MimeMessage.LoadAsync(stream, cancellationToken);
    }

    public async Task SetFlagsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        var patch = new Dictionary<string, object>();
        if (flags.HasFlag(MessageFlags.Seen))
        {
            patch["isRead"] = add;
        }

        if (flags.HasFlag(MessageFlags.Flagged))
        {
            patch["flag"] = new { flagStatus = add ? "flagged" : "notFlagged" };
        }

        // Answered/forwarded: the property other mail programs show the arrow icons from.
        if (add && (flags.HasFlag(MessageFlags.Answered) || flags.HasFlag(MessageFlags.Forwarded)))
        {
            patch["singleValueExtendedProperties"] = new[] { new { id = LastVerbProperty, value = flags.HasFlag(MessageFlags.Forwarded) ? "104" : "102" } };
        }

        if (patch.Count == 0)
        {
            return;
        }

        foreach (var id in remoteIds)
        {
            await graph.SendJsonAsync(HttpMethod.Patch, $"me/messages/{id}", patch, cancellationToken);
        }
    }

    public async Task MoveAsync(MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        ArgumentNullException.ThrowIfNull(target);
        foreach (var id in remoteIds)
        {
            await graph.SendJsonAsync(HttpMethod.Post, $"me/messages/{id}/move", new { destinationId = target.RemoteId }, cancellationToken);
        }
    }

    public async Task DeleteAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        foreach (var id in remoteIds)
        {
            await graph.SendJsonAsync(HttpMethod.Post, $"me/messages/{id}/permanentDelete", null, cancellationToken);
        }
    }

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await graph.PostMimeAsync("me/sendMail", await MimeBytesAsync(message, cancellationToken), cancellationToken);
    }

    /// <summary>Graph keeps a message created from MIME as a draft – fine for "Entwürfe", the main use here.</summary>
    public async Task AppendAsync(MailFolder folder, MimeMessage message, MessageFlags flags, DateTimeOffset? receivedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(message);
        var created = await graph.PostMimeAsync($"me/mailFolders/{folder.RemoteId}/messages", await MimeBytesAsync(message, cancellationToken), cancellationToken);
        if (created.Str("id") is { } id && flags.HasFlag(MessageFlags.Seen))
        {
            await graph.SendJsonAsync(HttpMethod.Patch, $"me/messages/{id}", new { isRead = true }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MessageSummary>> FetchOlderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);
        // Known are the newest ones (the first sync's window): the next older ones follow them by date.
        var known = knownRemoteIds.ToHashSet(StringComparer.Ordinal);
        var page = await graph.GetAsync($"me/mailFolders/{folder.RemoteId}/messages?$select={SummaryFields}&$orderby=receivedDateTime desc&$top={count}&$skip={known.Count}", cancellationToken);
        return [.. page.Arr("value").Where(m => !known.Contains(m.Str("id")!)).Select(SummaryOf)];
    }

    public async Task<(IReadOnlyList<MessageSummary> Hits, bool IsTruncated)> SearchAsync(MailFolder folder, MailSearchQuery query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(query);
        var parts = new List<string>();
        static string Quote(string value) => "\"" + value.Trim().Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
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

        var search = Uri.EscapeDataString("\"" + string.Join(" AND ", parts).Replace("\"", "'", StringComparison.Ordinal) + "\"");
        var page = await graph.GetAsync($"me/mailFolders/{folder.RemoteId}/messages?$search={search}&$select={SummaryFields}&$top={limit}", cancellationToken);
        return ([.. page.Arr("value").Select(SummaryOf)], page.TryGetProperty("@odata.nextLink", out _));
    }

    /// <summary>No push for desktop apps (Graph notifications need a public web address): wait, then sync again.</summary>
    public Task WaitForChangesAsync(MailFolder folder, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);

    public async Task<MailFolder> CreateFolderAsync(string name, FolderRole role, CancellationToken cancellationToken = default)
    {
        var created = await graph.SendJsonAsync(HttpMethod.Post, "me/mailFolders", new { displayName = name }, cancellationToken);
        return new MailFolder(connectionId, created.Str("id")!, name, null, role);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string InitialDelta(string folderId, bool sinceDays)
    {
        var filter = sinceDays ? $"&$filter=receivedDateTime ge {DateTime.UtcNow.AddDays(-InitialDays):yyyy-MM-ddT00:00:00Z}" : string.Empty;
        return $"me/mailFolders/{folderId}/messages/delta?$select={SummaryFields}{filter}";
    }

    internal static MessageSummary SummaryOf(JsonElement m)
    {
        var from = m.Obj("from").Obj("emailAddress");
        return new MessageSummary(
            m.Str("id")!,
            m.Str("internetMessageId"),
            null,
            m.Str("subject") ?? string.Empty,
            from.Str("address") is { } address ? new MailAddress(from.Str("name"), address) : null,
            [.. m.Arr("toRecipients").Select(r => r.Obj("emailAddress")).Where(e => e.Str("address") is not null).Select(e => new MailAddress(e.Str("name"), e.Str("address")!))],
            DateTimeOffset.TryParse(m.Str("receivedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.UnixEpoch,
            FlagsOf(m),
            0,
            m.Bool("hasAttachments"),
            m.Str("bodyPreview"));
    }

    internal static MessageFlags FlagsOf(JsonElement m) =>
        (m.Bool("isRead") ? MessageFlags.Seen : MessageFlags.None)
        | (m.Bool("isDraft") ? MessageFlags.Draft : MessageFlags.None)
        | (m.Obj("flag").Str("flagStatus") == "flagged" ? MessageFlags.Flagged : MessageFlags.None);

    private static async Task<byte[]> MimeBytesAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        return stream.ToArray();
    }
}
