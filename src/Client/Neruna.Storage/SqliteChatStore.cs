using Microsoft.EntityFrameworkCore;
using Neruna.Core.Chat;

namespace Neruna.Storage;

public sealed class SqliteChatStore(IDbContextFactory<NerunaDbContext> contexts) : IChatStore
{
    public async Task<IReadOnlyList<ChatMessage>> GetAllAsync(string source, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.ChatMessages.AsNoTracking().Where(m => m.Source == source).OrderBy(m => m.Id).ToListAsync(cancellationToken);
        return rows.Select(r => new ChatMessage(r.Id, r.RoomId, r.SenderId, r.RecipientId, r.Text, DateTimeOffset.FromUnixTimeMilliseconds(r.SentUnixMs))).ToList();
    }

    public async Task AddAsync(string source, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var ids = messages.Select(m => m.Id).ToList();
        var existing = (await db.ChatMessages.Where(m => m.Source == source && ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var message in messages.Where(m => existing.Add(m.Id)))
        {
            db.ChatMessages.Add(new ChatMessageEntity
            {
                Source = source,
                Id = message.Id,
                RoomId = message.RoomId,
                SenderId = message.SenderId,
                RecipientId = message.RecipientId,
                Text = message.Text,
                SentUnixMs = message.SentAt.ToUnixTimeMilliseconds(),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteOlderThanAsync(string source, DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var limit = cutoff.ToUnixTimeMilliseconds();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.ChatMessages.Where(m => m.Source == source && m.SentUnixMs < limit).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ClearAsync(string source, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.ChatMessages.Where(m => m.Source == source).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ClearOthersAsync(IReadOnlyCollection<string> keep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var kept = keep.ToList();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.ChatMessages.Where(m => !kept.Contains(m.Source)).ExecuteDeleteAsync(cancellationToken);
    }
}
