using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Neruna.Core;
using Neruna.Core.Mail;

namespace Neruna.Storage;

public sealed class SqliteMailStore(IDbContextFactory<NerunaDbContext> contexts, MessageContentFiles files) : IMailStore, IMailSearchIndex
{
    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folders = await db.MailFolders.AsNoTracking().Where(f => f.ConnectionId == connectionId).ToListAsync(cancellationToken);
        return folders.OrderBy(f => DisplayOrder(f.Role)).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToModel)
            .ToList();
    }

    // Familiar order: special folders first, then the user's own folders alphabetically.
    private static int DisplayOrder(FolderRole role) => role switch
    {
        FolderRole.Inbox => 0,
        FolderRole.Drafts => 1,
        FolderRole.Sent => 2,
        FolderRole.Trash => 3,
        FolderRole.Junk => 4,
        FolderRole.Archive => 5,
        _ => 6,
    };

    public async Task<IReadOnlyList<MailFolder>> MergeFoldersAsync(Guid connectionId, IReadOnlyList<MailFolder> remoteFolders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteFolders);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var existing = await db.MailFolders.Where(f => f.ConnectionId == connectionId).ToDictionaryAsync(f => f.RemoteId, StringComparer.Ordinal, cancellationToken);
        var remoteIds = remoteFolders.Select(f => f.RemoteId).ToHashSet(StringComparer.Ordinal);

        var vanished = existing.Values.Where(f => !remoteIds.Contains(f.RemoteId)).ToList();
        db.MailFolders.RemoveRange(vanished);

        foreach (var remote in remoteFolders)
        {
            if (!existing.TryGetValue(remote.RemoteId, out var entity))
            {
                entity = new MailFolderEntity { ConnectionId = connectionId, RemoteId = remote.RemoteId, Name = remote.Name };
                db.MailFolders.Add(entity);
            }

            entity.Name = remote.Name;
            entity.ParentRemoteId = remote.ParentRemoteId;
            entity.Role = remote.Role;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var folder in vanished)
        {
            files.DeleteFolder(connectionId, folder.RemoteId);
        }

        return await GetFoldersAsync(connectionId, cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> GetMessageIdsAsync(Guid connectionId, string folderRemoteId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folderId = await FolderIdAsync(db, connectionId, folderRemoteId, cancellationToken);
        return await db.Messages.Where(m => m.FolderId == folderId).Select(m => m.RemoteId).ToListAsync(cancellationToken);
    }

    public async Task ApplySyncResultAsync(MailFolder folder, FolderSyncResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(result);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var folderEntity = await db.MailFolders.SingleAsync(f => f.ConnectionId == folder.ConnectionId && f.RemoteId == folder.RemoteId, cancellationToken);
        List<string> deletedIds;

        if (result.IsFullResync)
        {
            deletedIds = await db.Messages.Where(m => m.FolderId == folderEntity.Id).Select(m => m.RemoteId).ToListAsync(cancellationToken);
            await db.Messages.Where(m => m.FolderId == folderEntity.Id).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            deletedIds = [.. result.RemovedRemoteIds];
            foreach (var chunk in deletedIds.Chunk(500))
            {
                await db.Messages.Where(m => m.FolderId == folderEntity.Id && chunk.Contains(m.RemoteId)).ExecuteDeleteAsync(cancellationToken);
            }
        }

        var changedIds = result.AddedOrChanged.Select(m => m.RemoteId).Concat(result.FlagUpdates.Keys).ToList();
        var existing = new Dictionary<string, MessageEntity>(StringComparer.Ordinal);
        foreach (var chunk in changedIds.Chunk(500))
        {
            foreach (var entity in await db.Messages.Where(m => m.FolderId == folderEntity.Id && chunk.Contains(m.RemoteId)).ToListAsync(cancellationToken))
            {
                existing[entity.RemoteId] = entity;
            }
        }

        foreach (var summary in result.AddedOrChanged)
        {
            if (!existing.TryGetValue(summary.RemoteId, out var entity))
            {
                entity = new MessageEntity { FolderId = folderEntity.Id, RemoteId = summary.RemoteId, Subject = summary.Subject, ToJson = "[]" };
                db.Messages.Add(entity);
                existing[summary.RemoteId] = entity;
            }

            Fill(entity, summary);
        }

        foreach (var (remoteId, flags) in result.FlagUpdates)
        {
            if (existing.TryGetValue(remoteId, out var entity))
            {
                entity.Flags = flags;
            }
        }

        folderEntity.SyncState = result.NewSyncState;
        folderEntity.TotalCount = result.TotalCount;
        folderEntity.UnreadCount = result.UnreadCount;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        files.DeleteMessages(folder.ConnectionId, folder.RemoteId, deletedIds);
    }

    public async Task RemoveMessagesAsync(Guid connectionId, string folderRemoteId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIds);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folderId = await FolderIdAsync(db, connectionId, folderRemoteId, cancellationToken);
        var ids = remoteIds.ToList();

        var unreadRemoved = await db.Messages.CountAsync(m => m.FolderId == folderId && ids.Contains(m.RemoteId) && (m.Flags & MessageFlags.Seen) == 0, cancellationToken);
        var removed = await db.Messages.Where(m => m.FolderId == folderId && ids.Contains(m.RemoteId)).ExecuteDeleteAsync(cancellationToken);
        await db.MailFolders.Where(f => f.Id == folderId).ExecuteUpdateAsync(
            s => s.SetProperty(f => f.TotalCount, f => Math.Max(0, f.TotalCount - removed))
                  .SetProperty(f => f.UnreadCount, f => Math.Max(0, f.UnreadCount - unreadRemoved)),
            cancellationToken);

        files.DeleteMessages(connectionId, folderRemoteId, ids);
    }

    public async Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid connectionId, string folderRemoteId, int skip, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folderId = await FolderIdAsync(db, connectionId, folderRemoteId, cancellationToken);

        var entities = await db.Messages.AsNoTracking()
            .Where(m => m.FolderId == folderId)
            .OrderByDescending(m => m.DateUnixMs)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return entities.Select(ToModel).ToList();
    }

    public async Task UpdateFlagsAsync(Guid connectionId, string folderRemoteId, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folderId = await FolderIdAsync(db, connectionId, folderRemoteId, cancellationToken);
        var messages = await db.Messages.Where(m => m.FolderId == folderId && remoteIds.Contains(m.RemoteId)).ToListAsync(cancellationToken);

        var unreadBefore = messages.Count(m => !m.Flags.HasFlag(MessageFlags.Seen));
        foreach (var message in messages)
        {
            message.Flags = add ? message.Flags | flags : message.Flags & ~flags;
        }

        // Keep the unread badge consistent until the next sync corrects it from the server.
        var unreadDelta = messages.Count(m => !m.Flags.HasFlag(MessageFlags.Seen)) - unreadBefore;
        if (unreadDelta != 0)
        {
            var folder = await db.MailFolders.SingleAsync(f => f.Id == folderId, cancellationToken);
            folder.UnreadCount = Math.Max(0, folder.UnreadCount + unreadDelta);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Stream?> OpenMessageContentAsync(Guid connectionId, string folderRemoteId, string remoteId, CancellationToken cancellationToken = default)
    {
        var path = files.PathFor(connectionId, folderRemoteId, remoteId);
        Stream? stream = File.Exists(path) ? File.OpenRead(path) : null;
        return Task.FromResult(stream);
    }

    public async Task SaveMessageContentAsync(Guid connectionId, string folderRemoteId, string remoteId, Stream mime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mime);
        var path = files.PathFor(connectionId, folderRemoteId, remoteId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp file first so a crash never leaves a truncated message in the cache.
        var temp = path + ".tmp";
        await using (var file = File.Create(temp))
        {
            await mime.CopyToAsync(file, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
        await MarkContentAvailableAsync(connectionId, folderRemoteId, remoteId, cancellationToken);
    }

    // The text is here now: the indexer adds it (it had only sender, subject and recipients).
    private async Task MarkContentAvailableAsync(Guid connectionId, string folderRemoteId, string remoteId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folderId = await db.MailFolders.Where(f => f.ConnectionId == connectionId && f.RemoteId == folderRemoteId).Select(f => (long?)f.Id).SingleOrDefaultAsync(cancellationToken);
        await db.Messages.Where(m => m.FolderId == folderId && m.RemoteId == remoteId && (m.SearchLevel == 1 || m.SearchLevel == 4))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.SearchLevel, 2), cancellationToken);
    }

    // ── Search index (FTS5 table MessageSearch, rowid = Messages.Id) ──

    private static readonly string[] SearchColumns = ["$id", "$sender", "$recipients", "$subject", "$body", "$attachments"];

    public async Task<IReadOnlyList<SearchWork>> GetSearchWorkAsync(IReadOnlyCollection<Guid> connections, int level, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connections);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var ids = connections.ToList();
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.SearchLevel == level)
            .Join(db.MailFolders.Where(f => ids.Contains(f.ConnectionId)), m => m.FolderId, f => f.Id, (m, f) => new { Message = m, Folder = f })
            .OrderByDescending(r => r.Message.DateUnixMs)
            .Take(take)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new SearchWork(r.Message.Id, ToModel(r.Folder), ToModel(r.Message),
            File.Exists(files.PathFor(r.Folder.ConnectionId, r.Folder.RemoteId, r.Message.RemoteId))))];
    }

    public async Task SaveSearchEntriesAsync(IReadOnlyList<(long Id, SearchDocument Document, int Level)> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM MessageSearch WHERE rowid = $id";
        var deleteId = delete.CreateParameter();
        deleteId.ParameterName = "$id";
        delete.Parameters.Add(deleteId);
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO MessageSearch (rowid, Sender, Recipients, Subject, Body, Attachments) VALUES ($id, $sender, $recipients, $subject, $body, $attachments)";
        var values = SearchColumns.Select(name =>
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = name;
            insert.Parameters.Add(parameter);
            return parameter;
        }).ToArray();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE Messages SET SearchLevel = $level WHERE Id = $id";
        var updateLevel = update.CreateParameter();
        updateLevel.ParameterName = "$level";
        var updateId = update.CreateParameter();
        updateId.ParameterName = "$id";
        update.Parameters.Add(updateLevel);
        update.Parameters.Add(updateId);
        delete.Transaction = insert.Transaction = update.Transaction = transaction.GetDbTransaction();

        foreach (var (id, document, level) in entries)
        {
            deleteId.Value = id;
            await delete.ExecuteNonQueryAsync(cancellationToken);
            values[0].Value = id;
            values[1].Value = document.Sender;
            values[2].Value = document.Recipients;
            values[3].Value = document.Subject;
            values[4].Value = (object?)document.Body ?? DBNull.Value;
            values[5].Value = (object?)document.Attachments ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            updateLevel.Value = level;
            updateId.Value = id;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailSearchHit>> SearchIndexAsync(string match, IReadOnlyCollection<MailFolder> folders, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var connectionIds = folders.Select(f => f.ConnectionId).Distinct().ToList();
        var wanted = folders.Select(f => (f.ConnectionId, f.RemoteId)).ToHashSet();
        var folderEntities = (await db.MailFolders.AsNoTracking().Where(f => connectionIds.Contains(f.ConnectionId)).ToListAsync(cancellationToken))
            .Where(f => wanted.Contains((f.ConnectionId, f.RemoteId)))
            .ToDictionary(f => f.Id);
        var folderIds = folderEntities.Keys.ToList();
        var messages = await db.Messages
            .FromSqlRaw("SELECT * FROM Messages WHERE Id IN (SELECT rowid FROM MessageSearch WHERE MessageSearch MATCH {0})", match)
            .AsNoTracking()
            .Where(m => folderIds.Contains(m.FolderId))
            .OrderByDescending(m => m.DateUnixMs)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return [.. messages.Select(m => new MailSearchHit(ToModel(folderEntities[m.FolderId]), ToModel(m)))];
    }

    public async Task<SearchIndexStatus> GetSearchStatusAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var levels = await db.Messages
            .Join(db.MailFolders.Where(f => f.ConnectionId == connectionId), m => m.FolderId, f => f.Id, (m, _) => m.SearchLevel)
            .GroupBy(level => level)
            .Select(g => new { Level = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return new SearchIndexStatus(levels.Sum(l => l.Count), levels.Where(l => l.Level > 0).Sum(l => l.Count), levels.Where(l => l.Level == 3).Sum(l => l.Count));
    }

    public async Task ClearSearchIndexAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM MessageSearch WHERE rowid IN (SELECT m.Id FROM Messages m JOIN MailFolders f ON f.Id = m.FolderId WHERE f.ConnectionId = {0})", [connectionId], cancellationToken);
        var folderIds = db.MailFolders.Where(f => f.ConnectionId == connectionId).Select(f => f.Id);
        await db.Messages.Where(m => folderIds.Contains(m.FolderId) && m.SearchLevel != 0)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.SearchLevel, 0), cancellationToken);
    }

    public async Task<IReadOnlyList<(string RemoteId, long Size)>> GetMessagesWithoutContentAsync(Guid connectionId, string folderRemoteId, DateTimeOffset? since, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folderId = await db.MailFolders.Where(f => f.ConnectionId == connectionId && f.RemoteId == folderRemoteId).Select(f => (long?)f.Id).SingleOrDefaultAsync(cancellationToken);
        if (folderId is null)
        {
            return [];
        }

        var from = since?.ToUnixTimeMilliseconds() ?? long.MinValue;
        var candidates = await db.Messages.Where(m => m.FolderId == folderId && m.DateUnixMs >= from)
            .OrderByDescending(m => m.DateUnixMs).Select(m => new { m.RemoteId, m.Size }).ToListAsync(cancellationToken);
        return [.. candidates.Where(m => !File.Exists(files.PathFor(connectionId, folderRemoteId, m.RemoteId))).Take(take).Select(m => (m.RemoteId, m.Size))];
    }

    public async Task<int> RemoveMessageContentBeforeAsync(Guid connectionId, string folderRemoteId, DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folderId = await db.MailFolders.Where(f => f.ConnectionId == connectionId && f.RemoteId == folderRemoteId).Select(f => (long?)f.Id).SingleOrDefaultAsync(cancellationToken);
        var until = before.ToUnixTimeMilliseconds();
        var old = folderId is null ? [] : await db.Messages.Where(m => m.FolderId == folderId && m.DateUnixMs < until)
            .Select(m => m.RemoteId).ToListAsync(cancellationToken);
        var cached = old.Where(id => File.Exists(files.PathFor(connectionId, folderRemoteId, id))).ToList();
        files.DeleteMessages(connectionId, folderRemoteId, cached);
        return cached.Count;
    }

    private static async Task<long> FolderIdAsync(NerunaDbContext db, Guid connectionId, string folderRemoteId, CancellationToken cancellationToken) =>
        await db.MailFolders.Where(f => f.ConnectionId == connectionId && f.RemoteId == folderRemoteId).Select(f => (long?)f.Id).SingleOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException($"Folder '{folderRemoteId}' is not known.");

    private static void Fill(MessageEntity entity, MessageSummary summary)
    {
        entity.MessageId = summary.MessageId;
        entity.InReplyTo = summary.InReplyTo;
        entity.Subject = summary.Subject;
        entity.FromName = summary.From?.Name;
        entity.FromAddress = summary.From?.Address;
        entity.ToJson = JsonSerializer.Serialize(summary.To);
        entity.DateUnixMs = summary.Date.ToUnixTimeMilliseconds();
        entity.Flags = summary.Flags;
        entity.Size = summary.Size;
        entity.HasAttachments = summary.HasAttachments;
        entity.Preview = summary.Preview;
        entity.Security = summary.Security;
    }

    private static MailFolder ToModel(MailFolderEntity f) =>
        new(f.ConnectionId, f.RemoteId, f.Name, f.ParentRemoteId, f.Role, f.TotalCount, f.UnreadCount, f.SyncState);

    private static MessageSummary ToModel(MessageEntity m) => new(
        m.RemoteId,
        m.MessageId,
        m.InReplyTo,
        m.Subject,
        m.FromAddress is null ? null : new MailAddress(m.FromName, m.FromAddress),
        JsonSerializer.Deserialize<List<MailAddress>>(m.ToJson) ?? [],
        DateTimeOffset.FromUnixTimeMilliseconds(m.DateUnixMs),
        m.Flags,
        m.Size,
        m.HasAttachments,
        m.Preview,
        m.Security);
}
