using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Utils;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>
/// The UI's single entry point for mail. Knows no protocol: it pairs a <see cref="IMailProvider"/> from the
/// <see cref="ProviderRegistry"/> with the offline <see cref="IMailStore"/>.
/// </summary>
public sealed class MailController(
    IAccountStore accounts,
    IMailStore store,
    ProviderRegistry providers,
    ILogger<MailController> logger,
    SyncActivity? activity = null,
    ISettingsStore? settings = null)
{
    // Per sync run and account at most this much is fetched ahead for offline use; the next run goes on.
    private const int OfflineBatch = 200;
    private const long OfflineBytesPerRun = 100L * 1024 * 1024;
    private const long OfflineMaxMessageSize = 50L * 1024 * 1024;

    /// <summary>The folder list of a connection was read (new folders may be there); raised on the syncing thread.</summary>
    public event EventHandler<Guid>? FoldersSynced;

    /// <summary>One folder of a full sync is done – the UI can show it before the others (raised on the syncing thread).</summary>
    public event EventHandler<MailFolder>? FolderSynced;

    public Task<SyncReport> SyncAllAsync(CancellationToken cancellationToken = default) =>
        SyncReport.ForEachConnectionAsync(accounts, ServiceKind.Mail, SyncConnectionAsync, logger, cancellationToken, activity);

    public async Task SyncConnectionAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateMail(connection);

        var remoteFolders = await provider.GetFoldersAsync(cancellationToken);
        var folders = await store.MergeFoldersAsync(connection.Id, remoteFolders, cancellationToken);
        FoldersSynced?.Invoke(this, connection.Id);

        // Inbox first so the user sees new mail as early as possible; each folder shows up as soon as it is done
        // (the first sync of a large mailbox takes a while).
        foreach (var folder in folders.OrderBy(f => f.Role == FolderRole.Inbox ? 0 : 1))
        {
            await SyncFolderAsync(provider, folder, cancellationToken);
            FolderSynced?.Invoke(this, folder);
        }

        await KeepOfflineAsync(provider, connection, folders, cancellationToken);
    }

    /// <summary>How far back the account keeps its mail complete on this device (Einstellungen → Konten).</summary>
    public async Task<OfflinePeriod> GetOfflinePeriodAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        settings is not null && Enum.TryParse<OfflinePeriod>(await settings.GetAsync(OfflinePeriods.Key(accountId), cancellationToken), out var period)
            ? period
            : OfflinePeriods.Default;

    public Task SetOfflinePeriodAsync(Guid accountId, OfflinePeriod period, CancellationToken cancellationToken = default) =>
        settings?.SetAsync(OfflinePeriods.Key(accountId), period == OfflinePeriods.Default ? null : period.ToString(), cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// After a sync: the content of the account's mail within its offline period is fetched ahead (newest first, the
    /// inbox first, a limited amount per run), older content leaves the device – the messages stay in the list and
    /// are fetched again when opened. Trash and junk are not fetched ahead.
    /// </summary>
    private async Task KeepOfflineAsync(IMailProvider provider, ServiceConnection connection, IReadOnlyList<MailFolder> folders, CancellationToken cancellationToken)
    {
        // Mail in a file on this device (Outlook data file) is there anyway: copying it would only double it.
        if (settings is null || provider.Capabilities.HasFlag(MailProviderCapabilities.LocalContent)
            || (await accounts.GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.Connections.Any(c => c.Id == connection.Id)) is not { } account)
        {
            return;
        }

        var period = await GetOfflinePeriodAsync(account.Id, cancellationToken);
        if (period == OfflinePeriod.OpenedOnly)
        {
            return;
        }

        var since = OfflinePeriods.Since(period, DateTimeOffset.UtcNow);
        var count = 0;
        var bytes = 0L;
        foreach (var folder in folders.OrderBy(f => f.Role == FolderRole.Inbox ? 0 : 1))
        {
            if (since is { } oldest)
            {
                await store.RemoveMessageContentBeforeAsync(folder.ConnectionId, folder.RemoteId, oldest, cancellationToken);
            }

            if (folder.Role is FolderRole.Trash or FolderRole.Junk || count >= OfflineBatch || bytes >= OfflineBytesPerRun)
            {
                continue;
            }

            foreach (var (remoteId, size) in await store.GetMessagesWithoutContentAsync(folder.ConnectionId, folder.RemoteId, since, OfflineBatch - count, cancellationToken))
            {
                if (size > OfflineMaxMessageSize)
                {
                    continue; // very large: only when opened
                }

                try
                {
                    var message = await provider.GetMessageAsync(folder, remoteId, cancellationToken);
                    using var buffer = new MemoryStream();
                    await message.WriteToAsync(buffer, cancellationToken);
                    buffer.Position = 0;
                    await store.SaveMessageContentAsync(folder.ConnectionId, folder.RemoteId, remoteId, buffer, cancellationToken);
                    count++;
                    bytes += buffer.Length;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogDebug(ex, "Message {RemoteId} in {Folder} not kept offline", remoteId, folder.RemoteId);
                }

                if (bytes >= OfflineBytesPerRun)
                {
                    break;
                }
            }
        }

        if (count > 0)
        {
            logger.LogInformation("Kept {Count} messages ({Bytes} bytes) of {Account} offline", count, bytes, account.Id);
        }
    }

    /// <summary>The account's out-of-office reply as its server has it, and what the server can do.</summary>
    /// <exception cref="AutoReplyUnavailableException">Not offered by this account's server (the message says why).</exception>
    /// <remarks>
    /// A series on a server without weekdays (see <see cref="AutoReplySeries"/>): returned as the series, and its next
    /// period set on the server once the last one has passed – this is where Neruna renews it.
    /// </remarks>
    public async Task<(AutoReply Reply, AutoReplyFeatures Features)> GetAutoReplyAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var provider = providers.CreateMail(connection);
        if (provider is not IAutoReplyProvider replies)
        {
            throw new AutoReplyUnavailableException(T("Für dieses Konto kann Neruna keine Abwesenheitsnotiz einrichten."));
        }

        var (reply, features) = await replies.GetAutoReplyAsync(cancellationToken);
        if (features.HasFlag(AutoReplyFeatures.Weekly) || settings is null
            || await AutoReplySeries.GetAsync(settings, connection.Id, cancellationToken) is not { } days)
        {
            return (reply, features);
        }

        // Switched off or set to something else elsewhere (webmail, another program): the series is over.
        if (!reply.IsEnabled || !reply.IsScheduled || !IsSeriesPeriod(reply, days))
        {
            await AutoReplySeries.SetAsync(settings, connection.Id, null, cancellationToken);
            return (reply, features);
        }

        var now = DateTimeOffset.Now;
        if (reply.End <= now && await accounts.GetAccountsAsync(cancellationToken) is var all
            && all.FirstOrDefault(a => a.Connections.Any(c => c.Id == connection.Id)) is { } account)
        {
            var (start, end) = AutoReplySeries.NextPeriod(days, now);
            reply = reply with { Start = start, End = end };
            await replies.SetAutoReplyAsync(reply, OwnAddresses(account), cancellationToken);
            logger.LogInformation("Out-of-office series of {Account} renewed for {Start:d}", account.Title, start);
        }

        return (reply with { Weekdays = days }, features);
    }

    // A period that the series set: whole local days, all of them days of the series.
    private static bool IsSeriesPeriod(AutoReply reply, IReadOnlyList<DayOfWeek> days)
    {
        var first = reply.Start!.Value.ToLocalTime();
        var end = reply.End!.Value.ToLocalTime();
        if (first.TimeOfDay != TimeSpan.Zero || end.TimeOfDay != TimeSpan.Zero || end <= first)
        {
            return false;
        }

        for (var day = first.Date; day < end.Date; day = day.AddDays(1))
        {
            if (!days.Contains(day.DayOfWeek))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] OwnAddresses(Account account) => [.. account.Identities.Select(i => i.Email).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Stores the out-of-office reply on the server; mail to any of the account's addresses is answered.</summary>
    /// <remarks>A series where the server has no weekdays: its next period goes to the server, the days stay here.</remarks>
    /// <exception cref="AutoReplyUnavailableException">Not offered by this account's server (the message says why).</exception>
    public async Task SetAutoReplyAsync(Account account, ServiceConnection connection, AutoReply reply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(reply);
        await using var provider = providers.CreateMail(connection);
        if (provider is not IAutoReplyProvider replies)
        {
            throw new AutoReplyUnavailableException(T("Für dieses Konto kann Neruna keine Abwesenheitsnotiz einrichten."));
        }

        if (reply.IsRecurring && reply.IsEnabled)
        {
            var (_, features) = await replies.GetAutoReplyAsync(cancellationToken);
            if (!features.HasFlag(AutoReplyFeatures.Weekly))
            {
                if (!features.HasFlag(AutoReplyFeatures.Schedule) || settings is null)
                {
                    throw new AutoReplyUnavailableException(T("Dieser Mailserver kann die Abwesenheitsnotiz nicht nur an bestimmten Tagen senden."));
                }

                var (start, end) = AutoReplySeries.NextPeriod(reply.Weekdays!, DateTimeOffset.Now);
                await replies.SetAutoReplyAsync(reply with { Start = start, End = end, Weekdays = null }, OwnAddresses(account), cancellationToken);
                await AutoReplySeries.SetAsync(settings, connection.Id, reply.Weekdays, cancellationToken);
                return;
            }
        }

        await replies.SetAutoReplyAsync(reply, OwnAddresses(account), cancellationToken);
        if (settings is not null)
        {
            await AutoReplySeries.SetAsync(settings, connection.Id, null, cancellationToken);
        }
    }

    /// <summary>The account's server rules for incoming mail ("Serverregeln").</summary>
    /// <exception cref="ServerRulesUnavailableException">Not offered by this account's server (the message says why).</exception>
    public async Task<ServerRuleSet> GetServerRulesAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateMail(connection);
        return provider is IServerRulesProvider rules
            ? await rules.GetRulesAsync(cancellationToken)
            : throw new ServerRulesUnavailableException(T("Für dieses Konto kann Neruna keine Serverregeln verwalten."));
    }

    /// <summary>Stores the rules on the server, in this order (rules made elsewhere that Neruna cannot show stay).</summary>
    /// <exception cref="ServerRulesUnavailableException">Refused or not offered (the message says why).</exception>
    public async Task SaveServerRulesAsync(ServiceConnection connection, IReadOnlyList<ServerRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        await using var provider = providers.CreateMail(connection);
        if (provider is not IServerRulesProvider server)
        {
            throw new ServerRulesUnavailableException(T("Für dieses Konto kann Neruna keine Serverregeln verwalten."));
        }

        await server.SaveRulesAsync(rules, cancellationToken);
    }

    /// <summary>"Ordner abonnieren": every folder of the connection's server and whether it is shown.</summary>
    /// <exception cref="NotSupportedException">The provider has no subscriptions.</exception>
    public async Task<IReadOnlyList<FolderSubscription>> GetSubscriptionsAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateMail(connection);
        return await provider.GetSubscriptionsAsync(cancellationToken);
    }

    /// <summary>Shows exactly these folders (subscribed on the server); hidden ones leave this device with their mail.</summary>
    public async Task SetSubscriptionsAsync(ServiceConnection connection, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        await using (var provider = providers.CreateMail(connection))
        {
            await provider.SetSubscriptionsAsync(remoteIds, cancellationToken);
        }

        await SyncConnectionAsync(connection, cancellationToken);
    }

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        store.GetFoldersAsync(connectionId, cancellationToken);

    /// <summary>Characters the account's server does not take in folder names (checked while typing).</summary>
    public async Task<string> GetForbiddenFolderCharactersAsync(ServiceConnection connection)
    {
        await using var provider = providers.CreateMail(connection);
        return provider.ForbiddenFolderNameCharacters;
    }

    /// <summary>A new folder under <paramref name="parent"/> (null: at the top of the account); the folder list follows.</summary>
    public async Task CreateFolderAsync(ServiceConnection connection, string name, MailFolder? parent, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateMail(connection);
        await provider.CreateSubfolderAsync(name, parent, cancellationToken);
        await RefreshFoldersAsync(provider, connection, cancellationToken);
    }

    /// <summary>Renames a folder on the server; its mail is loaded again under the new name.</summary>
    public async Task RenameFolderAsync(ServiceConnection connection, MailFolder folder, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        await using var provider = providers.CreateMail(connection);
        await provider.RenameFolderAsync(folder, name, cancellationToken);
        foreach (var current in await RefreshFoldersAsync(provider, connection, cancellationToken))
        {
            if (current.Role == FolderRole.None && (current.Name == name || current.RemoteId == folder.RemoteId))
            {
                await SyncFolderAsync(provider, current, cancellationToken);
            }
        }
    }

    /// <summary>How many messages are in the folder on the server (asked before deleting it).</summary>
    public async Task<int> CountFolderMessagesAsync(ServiceConnection connection, MailFolder folder, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateMail(connection);
        return (await provider.GetAllRemoteIdsAsync(folder, cancellationToken)).Count;
    }

    /// <summary>
    /// Deletes a folder: its messages go to the trash first (also those never loaded here), then the folder goes on
    /// the server. Only folders without subfolders.
    /// </summary>
    /// <exception cref="InvalidOperationException">There are messages but no trash to move them to.</exception>
    public async Task DeleteFolderAsync(ServiceConnection connection, MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        await using var provider = providers.CreateMail(connection);
        var ids = await provider.GetAllRemoteIdsAsync(folder, cancellationToken);
        var trash = await FindFolderAsync(folder.ConnectionId, FolderRole.Trash, cancellationToken);
        if (ids.Count > 0)
        {
            if (trash is null)
            {
                throw new InvalidOperationException(T("Dieses Konto hat keinen Papierkorb – die E-Mails im Ordner können nicht verschoben werden."));
            }

            await provider.MoveAsync(folder, ids, trash, cancellationToken);
        }

        await provider.DeleteFolderAsync(folder, cancellationToken);
        await RefreshFoldersAsync(provider, connection, cancellationToken);
        if (trash is not null && ids.Count > 0)
        {
            await SyncFolderAsync(provider, await CurrentAsync(trash, cancellationToken), cancellationToken);
        }
    }

    // The folder list from the server again (deleted and renamed folders leave with their stored mail).
    private async Task<IReadOnlyList<MailFolder>> RefreshFoldersAsync(IMailProvider provider, ServiceConnection connection, CancellationToken cancellationToken)
    {
        var folders = await store.MergeFoldersAsync(connection.Id, await provider.GetFoldersAsync(cancellationToken), cancellationToken);
        FoldersSynced?.Invoke(this, connection.Id);
        return folders;
    }

    public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(MailFolder folder, int skip = 0, int take = 200, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return store.GetMessagesAsync(folder.ConnectionId, folder.RemoteId, skip, take, cancellationToken);
    }

    /// <summary>Returns the full message, from the offline cache when possible.</summary>
    public async Task<MimeMessage> GetMessageAsync(ServiceConnection connection, MailFolder folder, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(folder);

        var cached = await store.OpenMessageContentAsync(folder.ConnectionId, folder.RemoteId, remoteId, cancellationToken);
        if (cached is not null)
        {
            await using (cached)
            {
                return await MimeMessage.LoadAsync(cached, cancellationToken);
            }
        }

        await using var provider = providers.CreateMail(connection);
        var message = await provider.GetMessageAsync(folder, remoteId, cancellationToken);

        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        await store.SaveMessageContentAsync(folder.ConnectionId, folder.RemoteId, remoteId, buffer, cancellationToken);

        return message;
    }

    public async Task SetFlagsAsync(ServiceConnection connection, MailFolder folder, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        await using var provider = providers.CreateMail(connection);
        await provider.SetFlagsAsync(folder, remoteIds, flags, add, cancellationToken);
        await store.UpdateFlagsAsync(folder.ConnectionId, folder.RemoteId, remoteIds, flags, add, cancellationToken);
    }

    /// <summary>"Alle als gelesen markieren": on the server for the whole folder, then the folder is synced (counts).</summary>
    public async Task MarkAllReadAsync(ServiceConnection connection, MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var loaded = await store.GetMessageIdsAsync(folder.ConnectionId, folder.RemoteId, cancellationToken);
        await using (var provider = providers.CreateMail(connection))
        {
            try
            {
                await provider.MarkAllReadAsync(folder, cancellationToken);
            }
            catch (NotSupportedException)
            {
                await provider.SetFlagsAsync(folder, loaded, MessageFlags.Seen, add: true, cancellationToken);
            }
        }

        await store.UpdateFlagsAsync(folder.ConnectionId, folder.RemoteId, loaded, MessageFlags.Seen, add: true, cancellationToken);
        await SyncFolderAsync(connection, folder, cancellationToken);
    }

    /// <summary>"Leeren" (trash, junk): every message deleted for good on the server, then the folder is synced.</summary>
    public async Task EmptyFolderAsync(ServiceConnection connection, MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var loaded = await store.GetMessageIdsAsync(folder.ConnectionId, folder.RemoteId, cancellationToken);
        await using (var provider = providers.CreateMail(connection))
        {
            try
            {
                await provider.EmptyAsync(folder, cancellationToken);
            }
            catch (NotSupportedException)
            {
                await provider.DeleteAsync(folder, loaded, cancellationToken);
            }
        }

        await store.RemoveMessagesAsync(folder.ConnectionId, folder.RemoteId, loaded, cancellationToken);
        await SyncFolderAsync(connection, folder, cancellationToken);
    }

    /// <summary>Moves messages on the server, drops them locally and refreshes the target folder.</summary>
    public async Task MoveAsync(ServiceConnection connection, MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        await using var provider = providers.CreateMail(connection);
        await provider.MoveAsync(source, remoteIds, target, cancellationToken);
        await store.RemoveMessagesAsync(source.ConnectionId, source.RemoteId, remoteIds, cancellationToken);
        await SyncFolderAsync(provider, await CurrentAsync(target, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Moves messages to a folder of <b>another account</b>. Servers cannot do that among themselves, so each message is
    /// downloaded, stored in the target (IMAP APPEND, with flags and received date) and only then removed from the
    /// source. One message at a time: an interruption leaves the moved ones moved and the rest untouched – at worst a
    /// duplicate, never a lost message.
    /// </summary>
    /// <returns>How many messages were moved (all, unless an exception is thrown).</returns>
    /// <exception cref="NotSupportedException">The target account cannot store messages (e.g. read-only demo account).</exception>
    public async Task<int> MoveToAccountAsync(
        ServiceConnection sourceConnection,
        MailFolder source,
        IReadOnlyCollection<MessageSummary> messages,
        ServiceConnection targetConnection,
        MailFolder target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceConnection);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(targetConnection);
        ArgumentNullException.ThrowIfNull(target);

        await using var targetProvider = providers.CreateMail(targetConnection);
        if (!targetProvider.Capabilities.HasFlag(MailProviderCapabilities.Append))
        {
            throw new NotSupportedException(T("Das Zielkonto kann keine Nachrichten ablegen."));
        }

        await using var sourceProvider = providers.CreateMail(sourceConnection);

        // From a read-only source (an Outlook data file) it is a copy: the original stays.
        var keepOriginal = !sourceProvider.Capabilities.HasFlag(MailProviderCapabilities.Delete);
        var moved = 0;
        try
        {
            foreach (var summary in messages)
            {
                var message = await GetMessageAsync(sourceConnection, source, summary.RemoteId, cancellationToken);
                await targetProvider.AppendAsync(target, message, summary.Flags, summary.Date, cancellationToken);

                // Only once the copy is stored in the target: remove the original.
                if (!keepOriginal)
                {
                    await sourceProvider.DeleteAsync(source, [summary.RemoteId], cancellationToken);
                    await store.RemoveMessagesAsync(source.ConnectionId, source.RemoteId, [summary.RemoteId], cancellationToken);
                }

                moved++;
            }
        }
        finally
        {
            if (moved > 0)
            {
                await SyncFolderAsync(targetProvider, await CurrentAsync(target, cancellationToken), cancellationToken);
            }
        }

        return moved;
    }

    /// <summary>Moves to "Gelöschte Elemente"; deletes permanently only inside the trash or without one.</summary>
    public async Task DeleteAsync(ServiceConnection connection, MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var trash = await FindFolderAsync(folder.ConnectionId, FolderRole.Trash, cancellationToken);
        if (trash is not null && trash.RemoteId != folder.RemoteId)
        {
            await MoveAsync(connection, folder, remoteIds, trash, cancellationToken);
            return;
        }

        await using var provider = providers.CreateMail(connection);
        await provider.DeleteAsync(folder, remoteIds, cancellationToken);
        await store.RemoveMessagesAsync(folder.ConnectionId, folder.RemoteId, remoteIds, cancellationToken);
    }

    /// <exception cref="InvalidOperationException">The account has no archive folder.</exception>
    public async Task ArchiveAsync(ServiceConnection connection, MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var archive = await FindFolderAsync(folder.ConnectionId, FolderRole.Archive, cancellationToken)
            ?? throw new InvalidOperationException(T("Dieses Konto hat keinen Archivordner."));
        await MoveAsync(connection, folder, remoteIds, archive, cancellationToken);
    }

    public async Task<MailFolder?> FindFolderAsync(Guid connectionId, FolderRole role, CancellationToken cancellationToken = default) =>
        (await store.GetFoldersAsync(connectionId, cancellationToken)).FirstOrDefault(f => f.Role == role);

    /// <summary>
    /// Loads up to <paramref name="count"/> messages older than the stored ones from the server (the first sync only
    /// takes the newest). They are added to the store; the folder's sync state stays as it is.
    /// </summary>
    /// <returns>How many were added (0: nothing older on the server).</returns>
    public async Task<int> LoadOlderAsync(ServiceConnection connection, MailFolder folder, int count = 500, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(folder);
        await using var provider = providers.CreateMail(connection);
        var gate = _folderLocks.GetOrAdd($"{folder.ConnectionId:N}|{folder.RemoteId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await CurrentAsync(folder, cancellationToken);
            var known = await store.GetMessageIdsAsync(folder.ConnectionId, folder.RemoteId, cancellationToken);
            var older = await provider.FetchOlderAsync(current, known, count, cancellationToken);
            if (older.Count > 0)
            {
                await store.ApplySyncResultAsync(current, new FolderSyncResult(
                    current.SyncState ?? string.Empty, false, older, new Dictionary<string, MessageFlags>(), [], current.TotalCount, current.UnreadCount), cancellationToken);
            }

            return older.Count;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>How many messages of the folder are stored locally (the list can show "1500 of 8200").</summary>
    public async Task<int> CountStoredAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return (await store.GetMessageIdsAsync(folder.ConnectionId, folder.RemoteId, cancellationToken)).Count;
    }

    /// <summary>
    /// Searches the given folders – on the server where the provider can (IMAP SEARCH: also messages never downloaded),
    /// otherwise in the stored summaries. Newest first; a folder that fails is reported, the others still count.
    /// </summary>
    public async Task<MailSearchResult> SearchAsync(IReadOnlyList<MailFolder> folders, MailSearchQuery query, int limitPerFolder = 300, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(query);
        var hits = new List<MailSearchHit>();
        var failed = new List<MailFolder>();
        var truncated = false;
        var connections = (await accounts.GetAccountsAsync(cancellationToken)).SelectMany(a => a.Connections).ToDictionary(c => c.Id);

        foreach (var group in folders.GroupBy(f => f.ConnectionId))
        {
            if (!connections.TryGetValue(group.Key, out var connection))
            {
                failed.AddRange(group);
                continue;
            }

            await using var provider = providers.CreateMail(connection);
            foreach (var folder in group)
            {
                try
                {
                    if (provider.Capabilities.HasFlag(MailProviderCapabilities.ServerSearch))
                    {
                        var (found, more) = await provider.SearchAsync(folder, query, limitPerFolder, cancellationToken);
                        hits.AddRange(found.Select(m => new MailSearchHit(folder, m)));
                        truncated |= more;
                    }
                    else
                    {
                        var stored = await store.GetMessagesAsync(folder.ConnectionId, folder.RemoteId, 0, int.MaxValue, cancellationToken);
                        var found = stored.Where(query.Matches).ToList();
                        hits.AddRange(found.Take(limitPerFolder).Select(m => new MailSearchHit(folder, m)));
                        truncated |= found.Count > limitPerFolder;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Searching {Folder} failed", folder.RemoteId);
                    failed.Add(folder);
                }
            }
        }

        return new MailSearchResult(hits.OrderByDescending(h => h.Message.Date).ToList(), failed, truncated);
    }

    /// <summary>Syncs a single folder, e.g. right after opening it.</summary>
    public async Task SyncFolderAsync(ServiceConnection connection, MailFolder folder, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateMail(connection);
        await SyncFolderAsync(provider, await CurrentAsync(folder, cancellationToken), cancellationToken);
    }

    public async Task SendAsync(ServiceConnection connection, MimeMessage message, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateMail(connection);
        if (!provider.Capabilities.HasFlag(MailProviderCapabilities.Send))
        {
            throw new NotSupportedException($"Provider '{connection.ProviderId}' cannot send mail.");
        }

        Privacy.OutgoingMail.Prepare(message, settings is not null && await settings.GetBoolAsync(SettingKeys.PrivacyHideTimeZone, cancellationToken: cancellationToken));

        // The account's Antwortadresse, unless the mail has its own Reply-To.
        if (message.ReplyTo.Count == 0
            && (await accounts.GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.Connections.Any(c => c.Id == connection.Id)) is { } account
            && (await ReplyAddress.GetAsync(settings, account, cancellationToken)).Address is { } replyTo
            && MailboxAddress.TryParse(replyTo, out var replyAddress))
        {
            message.ReplyTo.Add(replyAddress);
        }
        await provider.SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Stores the draft in the account's "Entwürfe" folder (created if the server has none) and replaces the previously
    /// saved version. The message keeps its Message-ID across saves; that is how the new copy is found again.
    /// </summary>
    /// <param name="previousRemoteId">The version saved before, if any; removed once the new one is stored.</param>
    /// <returns>The remote ID of the stored draft, or null if the server does not show it yet.</returns>
    public async Task<string?> SaveDraftAsync(ServiceConnection connection, MimeMessage draft, string? previousRemoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(draft);

        await using var provider = providers.CreateMail(connection);
        if (!provider.Capabilities.HasFlag(MailProviderCapabilities.Append))
        {
            throw new NotSupportedException(T("Dieses Konto kann keine Entwürfe speichern."));
        }

        draft.MessageId ??= Privacy.OutgoingMail.NewMessageId(draft.From.Mailboxes.FirstOrDefault()?.Address);
        var folder = await GetDraftsFolderAsync(provider, connection.Id, cancellationToken);
        await provider.AppendAsync(folder, draft, MessageFlags.Seen | MessageFlags.Draft, DateTimeOffset.Now, cancellationToken);

        // Only now that the new version is stored: remove the old one.
        if (previousRemoteId is not null)
        {
            await provider.DeleteAsync(folder, [previousRemoteId], cancellationToken);
            await store.RemoveMessagesAsync(connection.Id, folder.RemoteId, [previousRemoteId], cancellationToken);
        }

        await SyncFolderAsync(provider, await CurrentAsync(folder, cancellationToken), cancellationToken);
        var stored = await store.GetMessagesAsync(connection.Id, folder.RemoteId, 0, 1000, cancellationToken);
        return stored.FirstOrDefault(m => SameMessageId(m.MessageId, draft.MessageId))?.RemoteId;
    }

    /// <summary>Removes a saved draft for good (after sending, or when the user discards it).</summary>
    public async Task DeleteDraftAsync(ServiceConnection connection, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (await FindFolderAsync(connection.Id, FolderRole.Drafts, cancellationToken) is not { } folder)
        {
            return;
        }

        await using var provider = providers.CreateMail(connection);
        await provider.DeleteAsync(folder, [remoteId], cancellationToken);
        await store.RemoveMessagesAsync(connection.Id, folder.RemoteId, [remoteId], cancellationToken);
    }

    private async Task<MailFolder> GetDraftsFolderAsync(IMailProvider provider, Guid connectionId, CancellationToken cancellationToken)
    {
        if (await FindFolderAsync(connectionId, FolderRole.Drafts, cancellationToken) is { } existing)
        {
            return existing;
        }

        // The local list may just be outdated; otherwise create the folder, like other mail programs do.
        var remote = await provider.GetFoldersAsync(cancellationToken);
        if (remote.All(f => f.Role != FolderRole.Drafts))
        {
            await provider.CreateFolderAsync("Drafts", FolderRole.Drafts, cancellationToken);
            remote = await provider.GetFoldersAsync(cancellationToken);
        }

        var merged = await store.MergeFoldersAsync(connectionId, remote, cancellationToken);
        return merged.FirstOrDefault(f => f.Role == FolderRole.Drafts)
               ?? merged.First(f => string.Equals(f.Name, "Drafts", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameMessageId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim('<', '>', ' '), b.Trim('<', '>', ' '), StringComparison.Ordinal);

    // The UI may hold a folder with an outdated sync state; always continue from the stored one.
    private async Task<MailFolder> CurrentAsync(MailFolder folder, CancellationToken cancellationToken) =>
        (await store.GetFoldersAsync(folder.ConnectionId, cancellationToken)).FirstOrDefault(f => f.RemoteId == folder.RemoteId) ?? folder;

    // One sync per folder at a time: push (IDLE) and the periodic sync may want the same folder at once.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folderLocks = new(StringComparer.Ordinal);

    private async Task SyncFolderAsync(IMailProvider provider, MailFolder folder, CancellationToken cancellationToken)
    {
        var gate = _folderLocks.GetOrAdd($"{folder.ConnectionId:N}|{folder.RemoteId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // The sync state may have moved on while waiting.
            folder = await CurrentAsync(folder, cancellationToken);
            var known = await store.GetMessageIdsAsync(folder.ConnectionId, folder.RemoteId, cancellationToken);
            var result = await provider.SyncFolderAsync(folder, known, cancellationToken);
            await store.ApplySyncResultAsync(folder, result, cancellationToken);
            LogSynced(folder, result);
        }
        finally
        {
            gate.Release();
        }
    }

    private void LogSynced(MailFolder folder, FolderSyncResult result) =>
        logger.LogDebug("Synced {Folder}: {Changed} changed, {Removed} removed", folder.RemoteId, result.AddedOrChanged.Count, result.RemovedRemoteIds.Count);
}
