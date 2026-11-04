using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Notes;

/// <summary>
/// Notes on this computer and in the "Notes" folder of mailboxes (see docs/architecture.md, "Notizen"). The UI
/// speaks only to this controller. A note in a mailbox is saved here first (<see cref="NotePending.Save"/>) and
/// goes to the server with <see cref="PushAsync"/>: the new version is appended, then the old one removed. A note
/// changed on another device meanwhile is never overwritten: this device's version becomes a conflict copy.
/// </summary>
public sealed class NoteController
{
    /// <summary>Notebooks of the notes on this computer that hold no note yet (setting, JSON list).</summary>
    public const string LocalNotebooksKey = "notes.localNotebooks";

    /// <summary>The place new notes go to (connection id, or empty for this computer).</summary>
    public const string DefaultPlaceKey = "notes.defaultPlace";

    private readonly IAccountStore _accounts;
    private readonly IMailStore _mailStore;
    private readonly MailController _mail;
    private readonly ProviderRegistry _providers;
    private readonly INoteStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILogger<NoteController> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public NoteController(IAccountStore accounts, IMailStore mailStore, MailController mail, ProviderRegistry providers, INoteStore store,
        ISettingsStore settings, ILogger<NoteController> logger)
    {
        ArgumentNullException.ThrowIfNull(mail);
        _accounts = accounts;
        _mailStore = mailStore;
        _mail = mail;
        _providers = providers;
        _store = store;
        _settings = settings;
        _logger = logger;

        // A notes folder came in with the mail sync: its notes are read (and what waits here goes up).
        mail.FolderSynced += (_, folder) => _ = OnFolderSyncedAsync(folder);
    }

    /// <summary>Notes were added, changed or removed (also by a sync); raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>This computer, then every mailbox that can store messages (IMAP APPEND).</summary>
    public async Task<IReadOnlyList<NoteLocation>> GetLocationsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<NoteLocation> { new(null, T("Auf diesem Computer")) };
        foreach (var account in await _accounts.GetAccountsAsync(cancellationToken))
        {
            foreach (var connection in account.Connections.Where(c => c.Kind == ServiceKind.Mail))
            {
                if (await CanHoldNotesAsync(connection))
                {
                    result.Add(new NoteLocation(connection.Id, account.EmailAddress ?? account.DisplayName, account.Id));
                }
            }
        }

        return result;
    }

    private async Task<bool> CanHoldNotesAsync(ServiceConnection connection)
    {
        try
        {
            await using var provider = _providers.CreateMail(connection);
            return provider.Capabilities.HasFlag(MailProviderCapabilities.Append) && !provider.Capabilities.HasFlag(MailProviderCapabilities.LocalContent);
        }
        catch (InvalidOperationException)
        {
            return false; // no provider for it in this build
        }
    }

    /// <summary>Where new notes go: the chosen place if it still exists, else this computer.</summary>
    public async Task<Guid?> GetDefaultPlaceAsync(CancellationToken cancellationToken = default)
    {
        var value = await _settings.GetAsync(DefaultPlaceKey, cancellationToken);
        return Guid.TryParse(value, out var id) && (await GetLocationsAsync(cancellationToken)).Any(l => l.ConnectionId == id) ? id : null;
    }

    public Task SetDefaultPlaceAsync(Guid? connectionId, CancellationToken cancellationToken = default) =>
        _settings.SetAsync(DefaultPlaceKey, connectionId?.ToString(), cancellationToken);

    /// <summary>The notes of one place (null: this computer), or of all places; deleted ones waiting for the server not.</summary>
    public async Task<IReadOnlyList<Note>> GetNotesAsync(Guid? connectionId, bool allPlaces = false, CancellationToken cancellationToken = default) =>
        [.. (await _store.GetNotesAsync(connectionId, allPlaces, cancellationToken)).Where(n => n.Pending != NotePending.Delete)];

    public async Task<Note?> GetNoteAsync(Guid? connectionId, string id, CancellationToken cancellationToken = default) =>
        await _store.GetNoteAsync(connectionId, id, cancellationToken) is { Pending: not NotePending.Delete } note ? note : null;

    /// <summary>Notes linking to one of these: <see cref="NoteLinks.MailKey"/>, <see cref="NoteLinks.AddressKey"/> …</summary>
    public Task<IReadOnlyList<Note>> FindLinkedAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default) =>
        _store.FindByKeysAsync(keys, cancellationToken);

    /// <summary>
    /// Saves the note here at once (modified now); in a mailbox it is marked for the server and – with
    /// <paramref name="upload"/> – sent up right away (errors are logged; it goes up with the next sync).
    /// </summary>
    public async Task<Note> SaveAsync(Note note, bool upload = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        var saved = note with { Modified = DateTimeOffset.Now, Pending = note.IsLocal ? NotePending.None : NotePending.Save };
        await _store.SaveNoteAsync(saved, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        if (upload && saved.ConnectionId is { } connection)
        {
            await TryPushAsync(connection, cancellationToken);
        }

        return await _store.GetNoteAsync(saved.ConnectionId, saved.Id, cancellationToken) ?? saved;
    }

    /// <summary>Removes the note; in a mailbox it goes to the mailbox's trash (recoverable there).</summary>
    public async Task DeleteAsync(Note note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.ConnectionId is not { } connection || note.RemoteId is null)
        {
            await _store.DeleteNoteAsync(note.ConnectionId, note.Id, cancellationToken);
        }
        else
        {
            await _store.SaveNoteAsync(note with { Pending = NotePending.Delete }, cancellationToken);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (note.ConnectionId is { } place)
        {
            await TryPushAsync(place, cancellationToken);
        }
    }

    /// <summary>Into another notebook, also of another place (it keeps its id).</summary>
    public async Task<Note> MoveAsync(Note note, Guid? connectionId, string notebook, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.ConnectionId == connectionId)
        {
            return await SaveAsync(note with { Notebook = notebook }, cancellationToken: cancellationToken);
        }

        var moved = await SaveAsync(note with { ConnectionId = connectionId, Notebook = notebook, RemoteFolder = null, RemoteId = null, ConflictOf = null },
            cancellationToken: cancellationToken);
        await DeleteAsync(note, cancellationToken);
        return moved;
    }

    // ── Notebooks ──

    /// <summary>The notebooks of a place (paths, "Kunden/Müller"), without the main one ("").</summary>
    public async Task<IReadOnlyList<string>> GetNotebooksAsync(Guid? connectionId, CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (connectionId is { } id)
        {
            names.UnionWith(NoteFolders.All(await _mailStore.GetFoldersAsync(id, cancellationToken)).Select(n => n.Notebook));
        }
        else
        {
            names.UnionWith(await LocalNotebooksAsync(cancellationToken));
        }

        names.UnionWith((await GetNotesAsync(connectionId, cancellationToken: cancellationToken)).Select(n => n.Notebook));
        names.Remove(string.Empty);
        return [.. names.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>A new notebook; in a mailbox a folder under "Notes" (created first if missing).</summary>
    public async Task CreateNotebookAsync(Guid? connectionId, string path, CancellationToken cancellationToken = default)
    {
        path = NormalizeNotebook(path);
        if (path.Length == 0)
        {
            return;
        }

        if (connectionId is { } id)
        {
            var connection = await ConnectionAsync(id, cancellationToken);
            await EnsureFolderAsync(connection, path, cancellationToken);
        }
        else
        {
            var list = await LocalNotebooksAsync(cancellationToken);
            if (!list.Contains(path, StringComparer.Ordinal))
            {
                await _settings.SetAsync(LocalNotebooksKey, JsonSerializer.Serialize(list.Append(path).ToList()), cancellationToken);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes an empty notebook (with notes in it: <see cref="InvalidOperationException"/>).</summary>
    public async Task DeleteNotebookAsync(Guid? connectionId, string path, CancellationToken cancellationToken = default)
    {
        if ((await GetNotesAsync(connectionId, cancellationToken: cancellationToken)).Any(n => n.Notebook == path || n.Notebook.StartsWith(path + "/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(T("Das Notizbuch enthält noch Notizen."));
        }

        if (connectionId is { } id)
        {
            var connection = await ConnectionAsync(id, cancellationToken);
            var folders = NoteFolders.All(await _mailStore.GetFoldersAsync(id, cancellationToken));
            foreach (var (folder, _) in folders.Where(f => f.Notebook == path || f.Notebook.StartsWith(path + "/", StringComparison.Ordinal))
                         .OrderByDescending(f => f.Notebook.Length))
            {
                await _mail.DeleteFolderAsync(connection, folder, cancellationToken);
            }
        }
        else
        {
            var list = await LocalNotebooksAsync(cancellationToken);
            await _settings.SetAsync(LocalNotebooksKey,
                JsonSerializer.Serialize(list.Where(n => n != path && !n.StartsWith(path + "/", StringComparison.Ordinal)).ToList()), cancellationToken);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<string>> LocalNotebooksAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _settings.GetAsync(LocalNotebooksKey, cancellationToken) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<List<string>>(json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeNotebook(string path) =>
        string.Join("/", (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // ── Sync with the mailboxes ──

    /// <summary>Every mailbox: its notes folders are synced, waiting changes go up, the notes are read.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        foreach (var location in await GetLocationsAsync(cancellationToken))
        {
            if (location.ConnectionId is { } id)
            {
                try
                {
                    await SyncPlaceAsync(id, refreshFolders: true, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Notes of {Connection} not synced", id);
                }
            }
        }
    }

    /// <summary>Sends what waits for the server; errors (e.g. offline) are logged – the next sync tries again.</summary>
    public async Task TryPushAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await SyncPlaceAsync(connectionId, refreshFolders: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Notes of {Connection} wait for the server", connectionId);
        }
    }

    private async Task OnFolderSyncedAsync(MailFolder folder)
    {
        try
        {
            if (NoteFolders.IsNotesFolder(folder, await _mailStore.GetFoldersAsync(folder.ConnectionId)))
            {
                await SyncPlaceAsync(folder.ConnectionId, refreshFolders: false, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Notes of {Connection} not read after the mail sync", folder.ConnectionId);
        }
    }

    /// <summary>One mailbox: (its notes folders synced), waiting changes up, then the notes read into the store.</summary>
    public async Task SyncPlaceAsync(Guid connectionId, bool refreshFolders, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await ConnectionAsync(connectionId, cancellationToken);
            if (refreshFolders)
            {
                foreach (var (folder, _) in NoteFolders.All(await _mailStore.GetFoldersAsync(connectionId, cancellationToken)))
                {
                    await _mail.SyncFolderAsync(connection, folder, cancellationToken);
                }
            }

            var remote = await ReadRemoteAsync(connection, cancellationToken);
            if (await PushAsync(connection, remote, cancellationToken))
            {
                remote = await ReadRemoteAsync(connection, cancellationToken);
            }

            if (await ApplyAsync(connectionId, remote, cancellationToken))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record RemoteNote(MailFolder Folder, MessageSummary Message, Note Note);

    // The notes as the mail store has them now; known versions are taken from the note store, new ones parsed.
    private async Task<List<RemoteNote>> ReadRemoteAsync(ServiceConnection connection, CancellationToken cancellationToken)
    {
        var known = (await _store.GetNotesAsync(connection.Id, cancellationToken: cancellationToken))
            .Where(n => n.RemoteId is not null)
            .GroupBy(n => (n.RemoteFolder, n.RemoteId))
            .ToDictionary(g => g.Key, g => g.First());
        var result = new List<RemoteNote>();
        foreach (var (folder, notebook) in NoteFolders.All(await _mailStore.GetFoldersAsync(connection.Id, cancellationToken)))
        {
            foreach (var message in await _mailStore.GetMessagesAsync(connection.Id, folder.RemoteId, 0, 100_000, cancellationToken))
            {
                if (message.Flags.HasFlag(MessageFlags.Deleted))
                {
                    continue;
                }

                Note note;
                if (known.TryGetValue((folder.RemoteId, message.RemoteId), out var stored) && stored.ConflictOf is null)
                {
                    note = stored;
                }
                else
                {
                    try
                    {
                        var mime = await _mail.GetMessageAsync(connection, folder, message.RemoteId, cancellationToken);
                        note = NoteMime.FromMime(mime, connection.Id, notebook);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        _logger.LogInformation(ex, "Note {RemoteId} in {Folder} not readable", message.RemoteId, folder.RemoteId);
                        continue;
                    }
                }

                note = note with
                {
                    ConnectionId = connection.Id,
                    Notebook = notebook,
                    Pinned = message.Flags.HasFlag(MessageFlags.Flagged),
                    Tags = message.Keywords ?? [],
                    RemoteFolder = folder.RemoteId,
                    RemoteId = message.RemoteId,
                    Pending = NotePending.None,
                    ConflictOf = null,
                };
                result.Add(new RemoteNote(folder, message, note));
            }
        }

        return result;
    }

    // What waits on this device goes to the server; true when something was changed there.
    private async Task<bool> PushAsync(ServiceConnection connection, List<RemoteNote> remote, CancellationToken cancellationToken)
    {
        var waiting = (await _store.GetNotesAsync(connection.Id, cancellationToken: cancellationToken)).Where(n => n.Pending != NotePending.None).ToList();
        if (waiting.Count == 0)
        {
            return false;
        }

        var account = (await _accounts.GetAccountsAsync(cancellationToken)).First(a => a.Connections.Any(c => c.Id == connection.Id));
        await using var provider = _providers.CreateMail(connection);
        foreach (var note in waiting)
        {
            var current = note.RemoteId is null ? null : remote.FirstOrDefault(r => r.Folder.RemoteId == note.RemoteFolder && r.Message.RemoteId == note.RemoteId);
            if (note.Pending == NotePending.Delete)
            {
                if (current is not null)
                {
                    await RemoveAsync(connection, provider, current.Folder, current.Message.RemoteId, toTrash: true, cancellationToken);
                }

                await _store.DeleteNoteAsync(connection.Id, note.Id, cancellationToken);
                continue;
            }

            // Changed on another device since this version was read: this device's version becomes a copy.
            var upload = note;
            if (current is null && note.RemoteId is not null && remote.Any(r => r.Note.Id == note.Id))
            {
                upload = note with { Id = Guid.NewGuid().ToString("D").ToUpperInvariant(), ConflictOf = note.Id, RemoteFolder = null, RemoteId = null };
                await _store.DeleteNoteAsync(connection.Id, note.Id, cancellationToken);
                await _store.SaveNoteAsync(upload, cancellationToken);
                _logger.LogInformation("Note {Id} was changed on another device too: kept as a copy", note.Id);
            }

            var folder = await EnsureFolderAsync(connection, upload.Notebook, cancellationToken);
            var mime = NoteMime.ToMime(upload, account.EmailAddress);
            await provider.AppendAsync(folder, mime, MessageFlags.Seen | (upload.Pinned ? MessageFlags.Flagged : MessageFlags.None), upload.Modified, cancellationToken);

            // Only now that the new version is stored: the old one goes.
            if (current is not null && upload.Id == note.Id)
            {
                await RemoveAsync(connection, provider, current.Folder, current.Message.RemoteId, toTrash: false, cancellationToken);
            }

            await _mail.SyncFolderAsync(connection, folder, cancellationToken);
            var stored = (await _mailStore.GetMessagesAsync(connection.Id, folder.RemoteId, 0, 100_000, cancellationToken))
                .FirstOrDefault(m => SameMessageId(m.MessageId, mime.MessageId));
            if (stored is not null && upload.TagList.Count > 0 && provider.SupportsTags)
            {
                foreach (var tag in upload.TagList)
                {
                    await provider.SetTagAsync(folder, [stored.RemoteId], new MailTag(tag, tag, "#000000"), add: true, cancellationToken);
                }

                await _mail.SyncFolderAsync(connection, folder, cancellationToken);
            }

            await _store.SaveNoteAsync(upload with { RemoteFolder = stored is null ? null : folder.RemoteId, RemoteId = stored?.RemoteId, Pending = NotePending.None }, cancellationToken);
        }

        return true;
    }

    private async Task RemoveAsync(ServiceConnection connection, IMailProvider provider, MailFolder folder, string remoteId, bool toTrash, CancellationToken cancellationToken)
    {
        var trash = toTrash ? await _mail.FindFolderAsync(connection.Id, FolderRole.Trash, cancellationToken) : null;
        if (trash is not null && provider.Capabilities.HasFlag(MailProviderCapabilities.Move))
        {
            await provider.MoveAsync(folder, [remoteId], trash, cancellationToken);
        }
        else
        {
            await provider.DeleteAsync(folder, [remoteId], cancellationToken);
        }

        await _mailStore.RemoveMessagesAsync(connection.Id, folder.RemoteId, [remoteId], cancellationToken);
    }

    // The server's notes into the store: one note per id (the newest; older versions become copies), notes gone from
    // the server leave; what waits for the server on this device stays as it is. True when something changed.
    private async Task<bool> ApplyAsync(Guid connectionId, List<RemoteNote> remote, CancellationToken cancellationToken)
    {
        var stored = (await _store.GetNotesAsync(connectionId, cancellationToken: cancellationToken)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var group in remote.GroupBy(r => r.Note.Id, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(r => r.Note.Modified).ThenByDescending(r => r.Message.RemoteId, StringComparer.Ordinal).ToList();
            var notes = ordered.Select((r, i) => i == 0 ? r.Note : r.Note with { Id = r.Note.Id + "~" + r.Message.RemoteId, ConflictOf = r.Note.Id });
            foreach (var note in notes)
            {
                seen.Add(note.Id);
                if (stored.TryGetValue(note.Id, out var existing)
                    && (existing.Pending != NotePending.None
                        || (existing.RemoteId == note.RemoteId && existing.RemoteFolder == note.RemoteFolder && existing.Pinned == note.Pinned
                            && existing.TagList.SequenceEqual(note.TagList) && existing.Notebook == note.Notebook)))
                {
                    continue;
                }

                await _store.SaveNoteAsync(note, cancellationToken);
                changed = true;
            }
        }

        foreach (var gone in stored.Values.Where(n => n.Pending == NotePending.None && !seen.Contains(n.Id)))
        {
            await _store.DeleteNoteAsync(connectionId, gone.Id, cancellationToken);
            changed = true;
        }

        return changed;
    }

    // The folder of a notebook, created (with "Notes" and the folders above it) when missing.
    private async Task<MailFolder> EnsureFolderAsync(ServiceConnection connection, string notebook, CancellationToken cancellationToken)
    {
        var folders = await _mailStore.GetFoldersAsync(connection.Id, cancellationToken);
        if (NoteFolders.Root(folders) is not { } root)
        {
            await _mail.CreateFolderAsync(connection, NoteFolders.RootName, null, cancellationToken);
            folders = await _mailStore.GetFoldersAsync(connection.Id, cancellationToken);
            root = NoteFolders.Root(folders) ?? throw new InvalidOperationException(T("Der Notizordner konnte nicht angelegt werden."));
        }

        var current = root;
        var path = string.Empty;
        foreach (var part in NormalizeNotebook(notebook).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "/" + part;
            var existing = NoteFolders.All(folders).FirstOrDefault(f => f.Notebook == path).Folder;
            if (existing is null)
            {
                await _mail.CreateFolderAsync(connection, part, current, cancellationToken);
                folders = await _mailStore.GetFoldersAsync(connection.Id, cancellationToken);
                existing = NoteFolders.All(folders).FirstOrDefault(f => f.Notebook == path).Folder
                           ?? throw new InvalidOperationException(F("Notizbuch «{0}» konnte nicht angelegt werden.", path));
            }

            current = existing;
        }

        return current;
    }

    private async Task<ServiceConnection> ConnectionAsync(Guid connectionId, CancellationToken cancellationToken) =>
        (await _accounts.GetAccountsAsync(cancellationToken)).SelectMany(a => a.Connections).FirstOrDefault(c => c.Id == connectionId)
        ?? throw new InvalidOperationException(T("Das Konto dieser Notizen gibt es nicht mehr."));

    private static bool SameMessageId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim('<', '>', ' '), b.Trim('<', '>', ' '), StringComparison.Ordinal);
}
