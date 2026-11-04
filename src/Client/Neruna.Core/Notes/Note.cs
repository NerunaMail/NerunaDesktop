namespace Neruna.Core.Notes;

/// <summary>What still has to reach the server for a note in a mailbox.</summary>
public enum NotePending
{
    None,

    /// <summary>Changed on this device: the new version goes to the server at the next push.</summary>
    Save,

    /// <summary>Deleted on this device: removed on the server at the next push.</summary>
    Delete,
}

/// <summary>
/// A note. Its text is HTML (the same subset the mail editor writes); the first line is the title, as in the notes
/// apps of phones and of Nextcloud. <see cref="Id"/> stays across versions – on a mail server every save is a new
/// message (<see cref="RemoteId"/> changes), the id travels in the X-Universally-Unique-Identifier header.
/// </summary>
/// <param name="ConnectionId">The mailbox it lives in; null: only on this computer.</param>
/// <param name="Notebook">"" the main notebook, else a path ("Kunden", "Kunden/Müller").</param>
/// <param name="Tags">Keywords of the mail tags (Schlagwörter), shared with the mail.</param>
/// <param name="ConflictOf">A conflict copy: the id of the note it was changed alongside.</param>
/// <param name="RemoteFolder">The mail folder holding the current version (null: local or not uploaded yet).</param>
/// <param name="RemoteId">The message holding the current version.</param>
public sealed record Note(
    string Id,
    Guid? ConnectionId,
    string Notebook,
    string Html,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    bool Pinned = false,
    IReadOnlyList<string>? Tags = null,
    string? ConflictOf = null,
    string? RemoteFolder = null,
    string? RemoteId = null,
    NotePending Pending = NotePending.None)
{
    public IReadOnlyList<string> TagList => Tags ?? [];

    public bool IsLocal => ConnectionId is null;

    /// <summary>The first line of the text.</summary>
    public string Title => NoteText.Title(Html);

    /// <summary>The text after the title, shortened, for the list.</summary>
    public string Preview => NoteText.Preview(Html);

    /// <summary>A new, empty note.</summary>
    public static Note New(Guid? connectionId, string notebook, string html = "", DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        return new Note(Guid.NewGuid().ToString("D").ToUpperInvariant(), connectionId, notebook, html, at, at);
    }
}

/// <summary>Where notes are kept: on this computer, or in the "Notes" folder of a mailbox.</summary>
/// <param name="ConnectionId">null: on this computer.</param>
public sealed record NoteLocation(Guid? ConnectionId, string Name, Guid? AccountId = null)
{
    public bool IsLocal => ConnectionId is null;
}
