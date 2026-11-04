namespace Neruna.Core.Notes;

/// <summary>
/// The notes on this device: the local ones, and a copy of those in mailboxes (with what still has to go to the
/// server). Keys from <see cref="NoteLinks.Keys"/> are kept with each note to find a contact's or a mail's notes.
/// </summary>
public interface INoteStore
{
    /// <summary>The notes of one place (null connection: this computer), or of all places.</summary>
    Task<IReadOnlyList<Note>> GetNotesAsync(Guid? connectionId, bool allPlaces = false, CancellationToken cancellationToken = default);

    Task<Note?> GetNoteAsync(Guid? connectionId, string id, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces the note (by place and id).</summary>
    Task SaveNoteAsync(Note note, CancellationToken cancellationToken = default);

    Task DeleteNoteAsync(Guid? connectionId, string id, CancellationToken cancellationToken = default);

    /// <summary>Notes of any place having one of the keys.</summary>
    Task<IReadOnlyList<Note>> FindByKeysAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);
}
