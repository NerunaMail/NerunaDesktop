using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Mail;
using Neruna.Core.Notes;
using Neruna.Desktop.Editor;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

internal enum NoteSourceKind
{
    All,
    Pinned,
    Linked,
    Place,
    Notebook,
}

/// <summary>An entry on the left: all notes, pinned ones, a place (this computer, a mailbox) or one of its notebooks.</summary>
internal sealed class NoteSourceItem(NoteSourceKind kind, string label, Guid? connectionId = null, string? notebook = null, int depth = 0)
{
    public NoteSourceKind Kind { get; } = kind;

    public string Label { get; } = label;

    public Guid? ConnectionId { get; } = connectionId;

    /// <summary>Notebook path ("Kunden/Müller"); for a place null.</summary>
    public string? Notebook { get; } = notebook;

    public Thickness Indent { get; } = new(depth * 14, 0, 0, 0);

    public bool IsPlace => Kind == NoteSourceKind.Place;

    public string IconKey => Kind switch
    {
        NoteSourceKind.Pinned => "IconPin",
        NoteSourceKind.Place => ConnectionId is null ? "IconComputer" : "IconMail",
        NoteSourceKind.Notebook => "IconFolder",
        _ => "IconNote",
    };

    public bool Contains(Note note) => Kind switch
    {
        NoteSourceKind.Pinned => note.Pinned,
        NoteSourceKind.Place => note.ConnectionId == ConnectionId,
        NoteSourceKind.Notebook => note.ConnectionId == ConnectionId && (note.Notebook == Notebook || note.Notebook.StartsWith(Notebook + "/", StringComparison.Ordinal)),
        _ => true,
    };
}

/// <summary>A note in the list.</summary>
internal sealed partial class NoteItem(Note note, string placeName, IReadOnlyList<MailTag> tags) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Preview), nameof(DateText), nameof(IsPinned), nameof(IsWaiting), nameof(IsConflict))]
    public partial Note Note { get; set; } = note;

    public string PlaceName { get; } = placeName;

    public string Title => Note.Title.Length > 0 ? Note.Title : T("Neue Notiz");

    public string Preview => Note.Preview;

    public bool IsPinned => Note.Pinned;

    public bool IsConflict => Note.ConflictOf is not null;

    /// <summary>Changed here, not yet on the server.</summary>
    public bool IsWaiting => Note.Pending != NotePending.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    public partial IReadOnlyList<MailTag> Tags { get; set; } = tags;

    public bool HasTags => Tags.Count > 0;

    public string DateText
    {
        get
        {
            var local = Note.Modified.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("t", CultureInfo.CurrentCulture) : local.ToString("d", CultureInfo.CurrentCulture);
        }
    }

    public bool Matches(string query) =>
        query.Length == 0 || Note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                          || NoteText.PlainText(Note.Html).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                          || Tags.Any(t => t.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>A link in the open note (mail, contact, appointment), shown below it to open.</summary>
internal sealed record NoteLinkItem(NoteLink Link, string Text)
{
    public string IconKey => Link.Kind switch
    {
        NoteLinkKind.Mail => "IconMail",
        NoteLinkKind.Contact => "IconPeople",
        _ => "IconCalendar",
    };
}

/// <summary>A notebook to move the open note to.</summary>
internal sealed record NotebookChoice(Guid? ConnectionId, string Notebook, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A tag in the tag menu of the open note.</summary>
internal sealed partial class NoteTagChoice(MailTag tag, bool isSet) : ObservableObject
{
    public MailTag Tag { get; } = tag;

    [ObservableProperty]
    public partial bool IsSet { get; set; } = isSet;
}

/// <summary>
/// The notes page: places and notebooks on the left, the notes in the middle, the open note on the right in the mail
/// editor. Typing saves on this computer after a moment; a mailbox's note goes to the server when another note is
/// opened, the page is left, or after a quiet while.
/// </summary>
internal sealed partial class NotesViewModel : ViewModelBase
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan UploadDelay = TimeSpan.FromSeconds(20);

    private readonly NoteController _notes;
    private readonly MailTagService _tagService;
    private readonly ISettingsStore _settings;
    private readonly ILogger<NotesViewModel> _logger;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _uploadTimer;
    private IReadOnlyList<NoteLocation> _locations = [];
    private IReadOnlyList<Note> _all = [];
    private IReadOnlyList<MailTag> _knownTags = MailTagService.Defaults;
    private HashSet<string> _linkedIds = [];
    private IHtmlEditor? _editor;
    private Note? _current;
    private bool _isNew;
    private bool _dirty;
    private bool _syncing;

    public NotesViewModel(NoteController notes, MailTagService tagService, ISettingsStore settings, IFileService files, ILogger<NotesViewModel> logger)
    {
        _notes = notes;
        _tagService = tagService;
        _settings = settings;
        _logger = logger;
        Formatting = new FormattingViewModel(files) { ShowChecklist = true };
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveEditorAsync(upload: false);
        };
        _uploadTimer = new DispatcherTimer { Interval = UploadDelay };
        _uploadTimer.Tick += async (_, _) =>
        {
            _uploadTimer.Stop();
            await SaveEditorAsync(upload: true);
        };
        notes.Changed += (_, _) => Dispatcher.UIThread.Post(async () => await ReloadAsync());
        tagService.Changed += (_, _) => Dispatcher.UIThread.Post(async () => await ReloadAsync());
    }

    /// <summary>A link to a mail was clicked (its Message-ID): the shell shows the mail.</summary>
    public event EventHandler<string>? MailLinkRequested;

    /// <summary>A link to a contact was clicked: the shell shows it on the contacts page.</summary>
    public event EventHandler<NoteLink>? ContactLinkRequested;

    /// <summary>A link to an appointment was clicked (its UID): the shell opens it.</summary>
    public event EventHandler<string>? EventLinkRequested;

    /// <summary>For the status bar.</summary>
    public event EventHandler<string>? StatusMessage;

    public FormattingViewModel Formatting { get; }

    public ObservableCollection<NoteSourceItem> Sources { get; } = [];

    public ObservableCollection<NoteItem> Notes { get; } = [];

    public ObservableCollection<NoteLinkItem> Links { get; } = [];

    public ObservableCollection<NotebookChoice> Notebooks { get; } = [];

    public ObservableCollection<NoteTagChoice> TagChoices { get; } = [];

    public ObservableCollection<MailTag> CurrentTags { get; } = [];

    [ObservableProperty]
    public partial NoteSourceItem? SelectedSource { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(TogglePinCommand))]
    public partial NoteItem? SelectedNote { get; set; }

    public bool HasNote => SelectedNote is not null;

    [ObservableProperty]
    public partial NotebookChoice? SelectedNotebook { get; set; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>Below the note: where it is and when it was changed.</summary>
    [ObservableProperty]
    public partial string? NoteInfo { get; set; }

    /// <summary>The note is a copy made because it was changed on two devices.</summary>
    [ObservableProperty]
    public partial bool IsConflictCopy { get; set; }

    [ObservableProperty]
    public partial bool HasLinks { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>The selected entry on the left is a notebook that can be removed (when empty).</summary>
    public bool CanDeleteNotebook => SelectedSource is { Kind: NoteSourceKind.Notebook };

    // ── Loading ──

    /// <summary>The page comes to the front: notes from this computer at once, then the mailboxes' notes folders synced.</summary>
    public async Task ActivateAsync()
    {
        await ReloadAsync();
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            await Task.Run(() => _notes.SyncAsync());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Notes not synced");
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The page is left: the open note is saved and sent to the server.</summary>
    public Task LeaveAsync() => SaveEditorAsync(upload: true);

    public async Task ReloadAsync()
    {
        _locations = await _notes.GetLocationsAsync();
        _knownTags = await _tagService.GetAllAsync();
        _all = await _notes.GetNotesAsync(null, allPlaces: true);

        var selectedKey = SelectedSource is { } s ? (s.Kind, s.ConnectionId, s.Notebook) : (NoteSourceKind.All, (Guid?)null, (string?)null);
        var sources = new List<NoteSourceItem>
        {
            new(NoteSourceKind.All, T("Alle Notizen")),
            new(NoteSourceKind.Pinned, T("Angeheftet")),
        };
        if (_linkedIds.Count > 0)
        {
            sources.Add(new NoteSourceItem(NoteSourceKind.Linked, T("Verknüpfte Notizen")));
        }

        var notebooks = new List<NotebookChoice>();
        foreach (var location in _locations)
        {
            sources.Add(new NoteSourceItem(NoteSourceKind.Place, location.Name, location.ConnectionId));
            notebooks.Add(new NotebookChoice(location.ConnectionId, string.Empty, location.Name));
            foreach (var path in await _notes.GetNotebooksAsync(location.ConnectionId))
            {
                var depth = path.Count(c => c == '/');
                sources.Add(new NoteSourceItem(NoteSourceKind.Notebook, path[(path.LastIndexOf('/') + 1)..], location.ConnectionId, path, depth + 1));
                notebooks.Add(new NotebookChoice(location.ConnectionId, path, location.Name + " › " + path.Replace("/", " › ", StringComparison.Ordinal)));
            }
        }

        Replace(Sources, sources);
        Replace(Notebooks, notebooks);
        SelectedSource = Sources.FirstOrDefault(i => (i.Kind, i.ConnectionId, i.Notebook) == selectedKey) ?? Sources[0];
        Filter();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    partial void OnSelectedSourceChanged(NoteSourceItem? value)
    {
        OnPropertyChanged(nameof(CanDeleteNotebook));
        DeleteNotebookCommand.NotifyCanExecuteChanged();
        Filter();
    }

    partial void OnSearchTextChanged(string value) => Filter();

    private void Filter()
    {
        var source = SelectedSource;
        var query = SearchText.Trim();
        var keep = SelectedNote?.Note.Id;
        var items = _all
            .Where(n => source is null || (source.Kind == NoteSourceKind.Linked ? _linkedIds.Contains(n.Id) : source.Contains(n)))
            .Select(n => new NoteItem(n, PlaceName(n.ConnectionId), TagsOf(n)))
            .Where(i => i.Matches(query))
            .OrderByDescending(i => i.IsPinned)
            .ThenByDescending(i => i.Note.Modified)
            .ToList();

        // A new note not saved yet stays in the list.
        if (_isNew && _current is not null && items.All(i => i.Note.Id != _current.Id))
        {
            items.Insert(0, new NoteItem(_current, PlaceName(_current.ConnectionId), TagsOf(_current)));
        }

        _filtering = true;
        Replace(Notes, items);
        var selected = Notes.FirstOrDefault(i => i.Note.Id == keep);
        SelectedNote = selected ?? (keep is null || _current is null ? Notes.FirstOrDefault() : null);
        _filtering = false;
        IsEmpty = Notes.Count == 0;

        // The open note changed elsewhere (another device) and not here: shown anew.
        if (selected is not null && _current is not null && !_dirty && selected.Note.Html != _current.Html)
        {
            ShowNote(selected.Note);
        }
        else if (selected is not null && _current is not null)
        {
            _current = _current with { Pinned = selected.Note.Pinned, Tags = selected.Note.Tags, Notebook = selected.Note.Notebook };
            ShowDetails(_current);
        }
        else if (SelectedNote is null && _current is not null && !_isNew)
        {
            ClearEditor();
        }
    }

    private bool _filtering;

    private string PlaceName(Guid? connectionId) => _locations.FirstOrDefault(l => l.ConnectionId == connectionId)?.Name ?? T("Auf diesem Computer");

    private List<MailTag> TagsOf(Note note) =>
        [.. note.TagList.Select(k => _knownTags.FirstOrDefault(t => string.Equals(t.Keyword, k, StringComparison.OrdinalIgnoreCase)) ?? new MailTag(k, k, "#7A7A7A", null))];

    // ── The open note ──

    /// <summary>The editor of the page (once its view is shown); the open note goes into it.</summary>
    public void AttachEditor(IHtmlEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (_editor != editor)
        {
            _editor = editor;
            editor.ContentChanged += (_, _) => OnEdited();
        }

        if (_current is not null)
        {
            _ = LoadEditorAsync(_current);
        }
    }

    partial void OnSelectedNoteChanged(NoteItem? oldValue, NoteItem? newValue)
    {
        if (newValue is null || newValue.Note.Id == _current?.Id)
        {
            return;
        }

        _ = SwitchAsync(newValue.Note);
    }

    private async Task SwitchAsync(Note note)
    {
        if (!_filtering || _current is not null)
        {
            await SaveEditorAsync(upload: true);
        }

        if (_isNew && _current is not null && _current.Id != note.Id)
        {
            // An empty new note is dropped.
            _isNew = false;
            var dropped = Notes.FirstOrDefault(i => i.Note.Id == _current.Id);
            if (dropped is not null && _all.All(n => n.Id != _current.Id))
            {
                Notes.Remove(dropped);
            }
        }

        ShowNote(note);
    }

    private void ShowNote(Note note)
    {
        _current = note;
        _dirty = false;
        ShowDetails(note);
        _ = LoadEditorAsync(note);
    }

    private void ShowDetails(Note note)
    {
        IsPinned = note.Pinned;
        IsConflictCopy = note.ConflictOf is not null;
        _settingNotebook = true;
        SelectedNotebook = Notebooks.FirstOrDefault(n => n.ConnectionId == note.ConnectionId && n.Notebook == note.Notebook);
        _settingNotebook = false;
        Replace(CurrentTags, TagsOf(note));
        var changed = note.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        NoteInfo = note.Pending != NotePending.None
            ? F("{0} · geändert {1} · wird noch übertragen", PlaceName(note.ConnectionId), changed)
            : F("{0} · geändert {1}", PlaceName(note.ConnectionId), changed);
        ShowLinks(note.Html);
    }

    private void ClearEditor()
    {
        _current = null;
        _dirty = false;
        NoteInfo = null;
        CurrentTags.Clear();
        Links.Clear();
        HasLinks = false;
    }

    private async Task LoadEditorAsync(Note note)
    {
        if (_editor is null)
        {
            return;
        }

        var font = await _settings.GetAsync(SettingKeys.ComposeFont) is { Length: > 0 } configured ? configured : FontCatalog.DefaultFont;
        var size = double.TryParse(await _settings.GetAsync(SettingKeys.ComposeFontSize), NumberStyles.Float, CultureInfo.InvariantCulture, out var pt) ? pt : 11;
        if (_current?.Id == note.Id)
        {
            Formatting.Attach(_editor, note.Html, font, size, T("Notiz schreiben – die erste Zeile ist der Titel"));
        }
    }

    private void OnEdited()
    {
        if (_current is null)
        {
            return;
        }

        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        _uploadTimer.Stop();
        _uploadTimer.Start();
    }

    // The editor's text into the note: saved here; with upload also sent to the mailbox.
    private async Task SaveEditorAsync(bool upload)
    {
        _saveTimer.Stop();
        if (upload)
        {
            _uploadTimer.Stop();
        }

        if (_current is not { } note || _editor is null)
        {
            return;
        }

        string html;
        try
        {
            html = _dirty ? await _editor.GetBodyHtmlAsync() ?? NoteText.FromPlainText(_editor.GetPlainText()) : note.Html;
        }
        catch (InvalidOperationException)
        {
            html = note.Html; // the editor's page is gone (view left): what was saved last
        }

        if (_current?.Id != note.Id)
        {
            return; // another note was opened meanwhile
        }

        try
        {
            if (html == note.Html && !(_isNew && _dirty))
            {
                _dirty = false;
                if (upload && note.ConnectionId is { } place && note.Pending != NotePending.None)
                {
                    await _notes.TryPushAsync(place);
                }

                return;
            }

            if (_isNew && NoteText.PlainText(html).Trim().Length == 0)
            {
                return;
            }

            _dirty = false;
            _isNew = false;
            var saved = await _notes.SaveAsync(note with { Html = html }, upload);
            if (_current?.Id == note.Id)
            {
                _current = saved with { Html = _dirty ? _current.Html : saved.Html };
                ShowDetails(_current);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Note not saved");
            StatusMessage?.Invoke(this, F("Notiz nicht gespeichert: {0}", ex.Message));
        }
    }

    private void ShowLinks(string html)
    {
        Links.Clear();
        foreach (Match match in AnchorPattern().Matches(html ?? string.Empty))
        {
            if (NoteLinks.Parse(match.Groups[1].Value) is { } link && Links.All(l => l.Link != link))
            {
                var text = WebUtility.HtmlDecode(TagPattern().Replace(match.Groups[2].Value, string.Empty)).Trim();
                Links.Add(new NoteLinkItem(link, text.Length > 0 ? text : link.Address ?? link.Value));
            }
        }

        HasLinks = Links.Count > 0;
    }

    [GeneratedRegex("""<a\b[^>]*\bhref\s*=\s*["']([^"']+)["'][^>]*>(.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [RelayCommand]
    private void OpenLink(NoteLinkItem item)
    {
        switch (item.Link.Kind)
        {
            case NoteLinkKind.Mail:
                MailLinkRequested?.Invoke(this, item.Link.Value);
                break;
            case NoteLinkKind.Contact:
                ContactLinkRequested?.Invoke(this, item.Link);
                break;
            default:
                EventLinkRequested?.Invoke(this, item.Link.Value);
                break;
        }
    }

    // ── Commands ──

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        var source = SelectedSource;
        var place = source is { Kind: NoteSourceKind.Place or NoteSourceKind.Notebook } ? source.ConnectionId : await _notes.GetDefaultPlaceAsync();
        var notebook = source is { Kind: NoteSourceKind.Notebook } ? source.Notebook! : string.Empty;
        await StartNoteAsync(Note.New(place, notebook) with { Pinned = source?.Kind == NoteSourceKind.Pinned }, saved: false);
    }

    /// <summary>A new note with this content (about a mail, a contact, an appointment) in the default place, saved at once.</summary>
    public async Task NewNoteAsync(string html)
    {
        _linkedIds = [];
        SearchText = string.Empty;
        await ReloadAsync();
        SelectedSource = Sources[0];
        var note = await _notes.SaveAsync(Note.New(await _notes.GetDefaultPlaceAsync(), string.Empty, html));
        await StartNoteAsync(note, saved: true);
    }

    private async Task StartNoteAsync(Note note, bool saved)
    {
        await SaveEditorAsync(upload: true);
        _isNew = !saved;
        _current = note;
        _filtering = true;
        var item = new NoteItem(note, PlaceName(note.ConnectionId), TagsOf(note));
        Notes.Insert(0, item);
        SelectedNote = item;
        _filtering = false;
        IsEmpty = false;
        ShowNote(note);
        if (saved)
        {
            await ReloadAsync();
        }

        _editor?.FocusEditor();
    }

    /// <summary>Opens a note (from a contact's notes).</summary>
    public async Task OpenAsync(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        await ShowLinkedAsync([note]);
    }

    /// <summary>Shows these notes (a mail's or contact's) under «Verknüpfte Notizen», the first one open.</summary>
    public async Task ShowLinkedAsync(IReadOnlyList<Note> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        await SaveEditorAsync(upload: true);
        _linkedIds = [.. notes.Select(n => n.Id)];
        SearchText = string.Empty;
        await ReloadAsync();
        SelectedSource = Sources.FirstOrDefault(s => s.Kind == NoteSourceKind.Linked) ?? Sources[0];
        SelectedNote = Notes.FirstOrDefault(i => notes.Count > 0 && i.Note.Id == notes[0].Id) ?? Notes.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasNote))]
    private async Task DeleteAsync()
    {
        if (_current is not { } note)
        {
            return;
        }

        var question = note.IsLocal
            ? T("Die Notiz wird endgültig gelöscht.")
            : T("Die Notiz wird in den Papierkorb des Postfachs verschoben – auch auf allen anderen Geräten.");
        if (!_isNew && !await Views.ChoiceDialog.ShowInFrontAsync(F("«{0}» löschen", note.Title.Length > 0 ? note.Title : T("Neue Notiz")), question,
                (T("Löschen"), true, false), (T("Abbrechen"), false, true)))
        {
            return;
        }

        _saveTimer.Stop();
        _uploadTimer.Stop();
        var wasNew = _isNew;
        _isNew = false;
        ClearEditor();
        if (!wasNew)
        {
            await _notes.DeleteAsync(note);
        }

        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNote))]
    private async Task TogglePinAsync()
    {
        if (_current is null)
        {
            return;
        }

        await SaveEditorAsync(upload: false);
        await SaveChangeAsync(_current! with { Pinned = !_current!.Pinned });
    }

    partial void OnSelectedNotebookChanged(NotebookChoice? value)
    {
        if (!_settingNotebook && value is not null && _current is { } note && (value.ConnectionId != note.ConnectionId || value.Notebook != note.Notebook))
        {
            _ = MoveAsync(note, value);
        }
    }

    private bool _settingNotebook;

    private async Task MoveAsync(Note note, NotebookChoice target)
    {
        await SaveEditorAsync(upload: false);
        try
        {
            var current = _current ?? note;
            if (_isNew)
            {
                _current = current with { ConnectionId = target.ConnectionId, Notebook = target.Notebook };
                return;
            }

            _current = await _notes.MoveAsync(current, target.ConnectionId, target.Notebook);
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusMessage?.Invoke(this, F("Notiz nicht verschoben: {0}", ex.Message));
        }
    }

    /// <summary>The tag menu of the open note: every tag, the set ones ticked.</summary>
    public void PrepareTagMenu()
    {
        Replace(TagChoices, _knownTags.Select(t => new NoteTagChoice(t, _current?.TagList.Contains(t.Keyword, StringComparer.OrdinalIgnoreCase) == true)));
    }

    [RelayCommand]
    private async Task ToggleTagAsync(NoteTagChoice choice)
    {
        if (_current is not { } note)
        {
            return;
        }

        await SaveEditorAsync(upload: false);
        note = _current ?? note;
        var tags = note.TagList.Where(k => !string.Equals(k, choice.Tag.Keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!note.TagList.Contains(choice.Tag.Keyword, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(choice.Tag.Keyword);
        }

        choice.IsSet = !choice.IsSet;
        await SaveChangeAsync(note with { Tags = tags });
    }

    private async Task SaveChangeAsync(Note note)
    {
        if (_isNew)
        {
            _current = note;
            ShowDetails(note);
            return;
        }

        try
        {
            _current = await _notes.SaveAsync(note);
            ShowDetails(_current);
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusMessage?.Invoke(this, F("Notiz nicht gespeichert: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private async Task NewNotebookAsync()
    {
        var source = SelectedSource;
        var place = source is { Kind: NoteSourceKind.Place or NoteSourceKind.Notebook } ? source.ConnectionId : await _notes.GetDefaultPlaceAsync();
        var parent = source is { Kind: NoteSourceKind.Notebook } ? source.Notebook + "/" : string.Empty;
        var name = await Views.InputDialog.ShowInFrontAsync(T("Neues Notizbuch"), F("Name des Notizbuchs in «{0}»", PlaceName(place) + (parent.Length > 0 ? " › " + parent.TrimEnd('/') : string.Empty)),
            string.Empty, T("Anlegen"), text => text.Trim().Length == 0 ? T("Bitte einen Namen eingeben.") : text.Contains('/', StringComparison.Ordinal) ? T("Der Name darf keinen Schrägstrich enthalten.") : null);
        if (name is null)
        {
            return;
        }

        try
        {
            await _notes.CreateNotebookAsync(place, parent + name.Trim());
            await ReloadAsync();
            SelectedSource = Sources.FirstOrDefault(s => s.Kind == NoteSourceKind.Notebook && s.ConnectionId == place && s.Notebook == parent + name.Trim()) ?? SelectedSource;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusMessage?.Invoke(this, F("Notizbuch nicht angelegt: {0}", ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteNotebook))]
    private async Task DeleteNotebookAsync()
    {
        if (SelectedSource is not { Kind: NoteSourceKind.Notebook } source)
        {
            return;
        }

        if (!await Views.ChoiceDialog.ShowInFrontAsync(F("Notizbuch «{0}» löschen", source.Label), T("Nur ein leeres Notizbuch kann gelöscht werden."),
                (T("Löschen"), true, false), (T("Abbrechen"), false, true)))
        {
            return;
        }

        try
        {
            await _notes.DeleteNotebookAsync(source.ConnectionId, source.Notebook!);
            SelectedSource = Sources[0];
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusMessage?.Invoke(this, ex.Message);
        }
    }

    /// <summary>New notes go to the selected place from now on.</summary>
    [RelayCommand]
    private async Task MakeDefaultPlaceAsync(NoteSourceItem item)
    {
        await _notes.SetDefaultPlaceAsync(item.ConnectionId);
        StatusMessage?.Invoke(this, F("Neue Notizen werden in «{0}» gespeichert.", item.Label));
    }
}
