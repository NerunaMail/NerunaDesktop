using Microsoft.EntityFrameworkCore;
using Neruna.Core.Notes;

namespace Neruna.Storage;

public sealed class SqliteNoteStore(IDbContextFactory<NerunaDbContext> contexts) : INoteStore
{
    public async Task<IReadOnlyList<Note>> GetNotesAsync(Guid? connectionId, bool allPlaces = false, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Notes.AsNoTracking().Where(n => allPlaces || n.ConnectionId == connectionId).ToListAsync(cancellationToken);
        return [.. rows.Select(ToNote)];
    }

    public async Task<Note?> GetNoteAsync(Guid? connectionId, string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.ConnectionId == connectionId && n.NoteId == id, cancellationToken);
        return row is null ? null : ToNote(row);
    }

    public async Task SaveNoteAsync(Note note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Notes.FirstOrDefaultAsync(n => n.ConnectionId == note.ConnectionId && n.NoteId == note.Id, cancellationToken);
        if (row is null)
        {
            row = new NoteEntity { NoteId = note.Id, ConnectionId = note.ConnectionId, Notebook = string.Empty, Html = string.Empty, Text = string.Empty, Tags = string.Empty, Keys = string.Empty };
            db.Notes.Add(row);
        }

        row.Notebook = note.Notebook;
        row.Html = note.Html;
        row.Text = NoteText.PlainText(note.Html);
        row.CreatedUnixMs = note.Created.ToUnixTimeMilliseconds();
        row.ModifiedUnixMs = note.Modified.ToUnixTimeMilliseconds();
        row.Pinned = note.Pinned;
        row.Tags = string.Join('\n', note.TagList);
        var keys = NoteLinks.Keys(note.Html);
        row.Keys = keys.Count == 0 ? string.Empty : "\n" + string.Join('\n', keys) + "\n";
        row.ConflictOf = note.ConflictOf;
        row.RemoteFolder = note.RemoteFolder;
        row.RemoteId = note.RemoteId;
        row.Pending = (int)note.Pending;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteNoteAsync(Guid? connectionId, string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Notes.Where(n => n.ConnectionId == connectionId && n.NoteId == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Note>> FindByKeysAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return [];
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Notes.AsNoTracking().Where(n => n.Keys != string.Empty && n.Pending != (int)NotePending.Delete).ToListAsync(cancellationToken);
        var wanted = keys.Select(k => "\n" + k + "\n").ToList();
        return [.. rows.Where(r => wanted.Any(k => r.Keys.Contains(k, StringComparison.Ordinal))).Select(ToNote)];
    }

    private static Note ToNote(NoteEntity row) => new(
        row.NoteId,
        row.ConnectionId,
        row.Notebook,
        row.Html,
        DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedUnixMs).ToLocalTime(),
        DateTimeOffset.FromUnixTimeMilliseconds(row.ModifiedUnixMs).ToLocalTime(),
        row.Pinned,
        row.Tags.Length == 0 ? [] : row.Tags.Split('\n'),
        row.ConflictOf,
        row.RemoteFolder,
        row.RemoteId,
        (NotePending)row.Pending);
}
