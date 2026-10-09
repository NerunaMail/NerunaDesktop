using Microsoft.EntityFrameworkCore;
using Neruna.Core.Chat;

namespace Neruna.Storage;

public sealed class SqliteChatStore(IDbContextFactory<NerunaDbContext> contexts) : IChatStore
{
    public async Task<IReadOnlyList<ChatMessage>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.ChatMessages.AsNoTracking().OrderBy(m => m.Id).ToListAsync(cancellationToken);
        return rows.Select(r => new ChatMessage(r.Id, r.RoomId, r.SenderId, r.RecipientId, r.Text, DateTimeOffset.FromUnixTimeMilliseconds(r.SentUnixMs))).ToList();
    }

    public async Task AddAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var ids = messages.Select(m => m.Id).ToList();
        var existing = (await db.ChatMessages.Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var message in messages.Where(m => existing.Add(m.Id)))
        {
            db.ChatMessages.Add(new ChatMessageEntity
            {
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

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var limit = cutoff.ToUnixTimeMilliseconds();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.ChatMessages.Where(m => m.SentUnixMs < limit).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.ChatMessages.ExecuteDeleteAsync(cancellationToken);
    }
}
