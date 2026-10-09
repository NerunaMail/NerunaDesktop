using Microsoft.EntityFrameworkCore;
using Neruna.Core.Mail;

namespace Neruna.Storage;

public sealed class SqliteTextTemplateStore(IDbContextFactory<NerunaDbContext> contexts) : ITextTemplateStore
{
    public async Task<IReadOnlyList<TextTemplate>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.TextTemplates.AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(r => new TextTemplate(r.Id, r.Name, r.Html, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedUnixMs), r.Source, r.Shortcut)).ToList();
    }

    public async Task SaveAsync(TextTemplate textTemplate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textTemplate);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.TextTemplates.FindAsync([textTemplate.Id], cancellationToken);
        if (row is null)
        {
            row = new TextTemplateEntity { Id = textTemplate.Id, Name = textTemplate.Name, Html = textTemplate.Html, Source = textTemplate.Source };
            db.TextTemplates.Add(row);
        }

        row.Name = textTemplate.Name;
        row.Html = textTemplate.Html;
        row.Source = textTemplate.Source;
        row.Shortcut = TextTemplate.NormalizeShortcut(textTemplate.Shortcut);
        row.UpdatedUnixMs = textTemplate.UpdatedAt.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.TextTemplates.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
