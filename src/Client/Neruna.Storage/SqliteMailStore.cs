using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Neruna.Core;
using Neruna.Core.Mail;

namespace Neruna.Storage;

public sealed class SqliteMailStore(IDbContextFactory<NerunaDbContext> contexts, MessageContentFiles files) : IMailStore
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
