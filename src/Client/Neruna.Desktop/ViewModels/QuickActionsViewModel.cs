using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A choice in a list (account, folder, template, stamp, kind …): what is stored and what is shown.</summary>
internal sealed record Choice<T>(T Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Settings → E-Mail → Schnellaktionen: the actions (button, conditions, steps) and the PDF stamps.</summary>
internal sealed partial class QuickActionsViewModel(QuickActionService service, IAccountStore accounts, MailController mail, TextTemplateService templates, IFileService files) : ViewModelBase
{
    private List<QuickAction> _actions = [];
    private bool _loadingStamps;

    public ObservableCollection<QuickActionItem> Actions { get; } = [];

    public bool HasActions => Actions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsListing))]
    public partial QuickActionEditor? Editing { get; set; }

    public bool IsEditing => Editing is not null;

    public bool IsListing => Editing is null;

    public ObservableCollection<StampEditor> Stamps { get; } = [];

    /// <summary>The name on stamps; empty: the person logged in to the computer (shown as placeholder).</summary>
    [ObservableProperty]
    public partial string StampName { get; set; } = string.Empty;

    public static string SystemName => SystemUserName.Get();

    partial void OnStampNameChanged(string value)
    {
        if (!_loadingStamps)
        {
            _ = service.SetStampNameAsync(value);
        }
    }

    public static IReadOnlyList<string> Colors => QuickActionService.Colors;

    public async Task ReloadAsync()
    {
        _actions = [.. await service.GetActionsAsync()];
        ShowActions();

        _loadingStamps = true;
        StampName = await service.GetStampNameAsync() ?? string.Empty;
        Stamps.Clear();
        foreach (var stamp in await service.GetStampsAsync())
        {
            Stamps.Add(NewStampEditor(stamp));
        }

        _loadingStamps = false;
    }

    private void ShowActions()
    {
        Actions.Clear();
        foreach (var action in _actions)
        {
            Actions.Add(new QuickActionItem(action, Describe(action), Warn(action)));
        }

        OnPropertyChanged(nameof(HasActions));
    }

    // Imported from another computer: a save folder that does not exist here is pointed out (to adjust it).
    private static string? Warn(QuickAction action)
    {
        var missing = action.Steps.Where(s => s.Kind == QuickActionStepKind.SaveAttachment && !string.IsNullOrWhiteSpace(s.Folder) && !Directory.Exists(s.Folder))
            .Select(s => s.Folder!).Distinct().ToList();
        return missing.Count == 0 ? null : F("Ordner nicht gefunden: {0} – bitte unter «Bearbeiten» anpassen.", string.Join(", ", missing));
    }

    private async Task<(Dictionary<Guid, string> Accounts, Dictionary<Guid, string> Templates)> LookupsAsync() =>
        ((await accounts.GetAccountsAsync()).Where(a => a.EmailAddress is not null).ToDictionary(a => a.Id, a => a.EmailAddress!),
         (await templates.GetAllAsync()).ToDictionary(t => t.Id, t => t.Name));

    /// <summary>Notices after export and import (shown above the list).</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_actions.Count == 0)
        {
            return;
        }

        var (accountAddresses, templateNames) = await LookupsAsync();
        var json = QuickActionService.Export(_actions, [.. Stamps.Select(s => s.ToStamp())], accountAddresses, templateNames);
        var target = await files.SaveFileAsync(T("Schnellaktionen exportieren"), T("Schnellaktionen") + ".nerunaactions");
        if (target is null)
        {
            return;
        }

        await using (target)
        await using (var writer = new StreamWriter(target))
        {
            await writer.WriteAsync(json);
        }

        Notice = _actions.Count == 1 ? T("1 Schnellaktion exportiert.") : F("{0} Schnellaktionen exportiert.", _actions.Count);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var paths = await files.PickFilesAsync(T("Schnellaktionen importieren"));
        if (paths.Count == 0)
        {
            return;
        }

        var (accountAddresses, templateNames) = await LookupsAsync();
        var count = 0;
        foreach (var path in paths)
        {
            try
            {
                var (imported, stamps) = QuickActionService.Import(await File.ReadAllTextAsync(path), accountAddresses, templateNames);
                foreach (var stamp in stamps.Where(s => Stamps.All(here => here.Id != s.Id)))
                {
                    Stamps.Add(NewStampEditor(stamp));
                }

                foreach (var action in imported)
                {
                    var index = _actions.FindIndex(a => a.Id == action.Id);
                    if (index >= 0)
                    {
                        _actions[index] = action; // the same action, a newer version
                    }
                    else
                    {
                        _actions.Add(action);
                    }

                    count++;
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                Notice = F("«{0}» ist keine Datei mit Schnellaktionen.", Path.GetFileName(path));
                return;
            }
        }

        await SaveStampsAsync();
        await service.SaveActionsAsync(_actions);
        ShowActions();
        Notice = (count == 1 ? T("1 Schnellaktion importiert.") : F("{0} Schnellaktionen importiert.", count))
            + (Actions.Any(a => a.HasWarning) ? " " + T("Bitte die markierten Ordner anpassen.") : string.Empty);
    }

    private static string Describe(QuickAction action)
    {
        var when = new List<string>();
        switch (action.When.Attachment)
        {
            case QuickActionAttachmentKind.Any: when.Add(T("mit Anhang")); break;
            case QuickActionAttachmentKind.Pdf: when.Add(T("mit PDF")); break;
            case QuickActionAttachmentKind.Image: when.Add(T("mit Bild")); break;
            case QuickActionAttachmentKind.Office: when.Add(T("mit Office-Datei")); break;
            case QuickActionAttachmentKind.Extension: when.Add(F("mit {0}", action.When.Extensions ?? string.Empty)); break;
        }

        if (!string.IsNullOrWhiteSpace(action.When.SenderContains))
        {
            when.Add(F("von «{0}»", action.When.SenderContains));
        }

        if (!string.IsNullOrWhiteSpace(action.When.SubjectContains))
        {
            when.Add(F("Betreff «{0}»", action.When.SubjectContains));
        }

        if (!string.IsNullOrWhiteSpace(action.When.FolderName))
        {
            when.Add(F("in «{0}»", action.When.FolderName));
        }

        var steps = string.Join(" → ", action.Steps.Select(s => StepEditor.TitleOf(s.Kind)));
        return (when.Count == 0 ? T("Bei jeder E-Mail") : string.Join(", ", when)) + (steps.Length > 0 ? ": " + steps : string.Empty);
    }

    [RelayCommand]
    private Task NewAsync() => EditAsync(null);

    [RelayCommand]
    private async Task EditAsync(QuickActionItem? item)
    {
        var options = await LoadOptionsAsync();
        var action = item?.Action ?? new QuickAction(Guid.NewGuid(), T("Neue Aktion"), Colors[0],
            new QuickActionCondition { Attachment = QuickActionAttachmentKind.Pdf }, []);
        Editing = new QuickActionEditor(action, options, files, SaveEditedAsync, () => Editing = null);
    }

    private async Task SaveEditedAsync(QuickAction action)
    {
        var index = _actions.FindIndex(a => a.Id == action.Id);
        if (index >= 0)
        {
            _actions[index] = action;
        }
        else
        {
            _actions.Add(action);
        }

        await service.SaveActionsAsync(_actions);
        Editing = null;
        ShowActions();
    }

    [RelayCommand]
    private async Task DeleteAsync(QuickActionItem? item)
    {
        if (item is not null && _actions.RemoveAll(a => a.Id == item.Action.Id) > 0)
        {
            await service.SaveActionsAsync(_actions);
            ShowActions();
        }
    }

    [RelayCommand]
    private Task MoveUpAsync(QuickActionItem? item) => MoveAsync(item, -1);

    [RelayCommand]
    private Task MoveDownAsync(QuickActionItem? item) => MoveAsync(item, 1);

    private async Task MoveAsync(QuickActionItem? item, int by)
    {
        var index = item is null ? -1 : _actions.FindIndex(a => a.Id == item.Action.Id);
        if (index < 0 || index + by < 0 || index + by >= _actions.Count)
        {
            return;
        }

        (_actions[index], _actions[index + by]) = (_actions[index + by], _actions[index]);
        await service.SaveActionsAsync(_actions);
        ShowActions();
    }

    // Accounts, folders, templates and stamps for the lists of the editor.
    private async Task<EditorOptions> LoadOptionsAsync()
    {
        var accountChoices = new List<Choice<Guid?>> { new(null, T("Alle Konten")) };
        var folders = new Dictionary<Guid, IReadOnlyList<FolderChoice>>();
        var allFolders = new List<FolderChoice>();
        foreach (var account in await accounts.GetAccountsAsync())
        {
            if (account.Connections.FirstOrDefault(c => c.Kind == ServiceKind.Mail) is not { } connection)
            {
                continue;
            }

            accountChoices.Add(new Choice<Guid?>(account.Id, account.Title));
            var tree = FolderTree(await mail.GetFoldersAsync(connection.Id), account.Title);
            folders[account.Id] = tree;
            allFolders.Add(new FolderChoice(null, account.Title, account.Title, 0, IsAccount: true));
            allFolders.AddRange(tree.Select(f => f with { Depth = f.Depth + 1 }));
        }

        var templateChoices = new List<Choice<Guid?>> { new(null, T("Keine")) };
        templateChoices.AddRange((await templates.GetAllAsync()).Select(t => new Choice<Guid?>(t.Id, t.Name)));
        var stampChoices = Stamps.Select(s => new Choice<Guid?>(s.Id, s.Text)).ToList();
        return new EditorOptions(accountChoices, folders, allFolders, templateChoices, stampChoices);
    }

    // As in the folder tree: special folders first, each with its subfolders, then the others by name.
    private static List<FolderChoice> FolderTree(IReadOnlyList<MailFolder> folders, string account)
    {
        var result = new List<FolderChoice>();
        var byParent = folders.ToLookup(f => f.ParentRemoteId);
        var known = folders.Select(f => f.RemoteId).ToHashSet(StringComparer.Ordinal);
        void Add(MailFolder folder, int depth, string parentPath)
        {
            var name = MailFolderNode.DisplayName(folder);
            var path = parentPath + " › " + name;
            result.Add(new FolderChoice(folder.RemoteId, name, path, depth));
            foreach (var child in byParent[folder.RemoteId].Where(f => f.Role == FolderRole.None).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Add(child, depth + 1, path);
            }
        }

        foreach (var folder in folders.Where(f => f.Role != FolderRole.None).OrderBy(f => f.Role))
        {
            Add(folder, 0, account);
        }

        foreach (var folder in folders.Where(f => f.Role == FolderRole.None && (f.ParentRemoteId is null || !known.Contains(f.ParentRemoteId))).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Add(folder, 0, account);
        }

        return result;
    }

    // ── Stamps: saved on every change ──

    private StampEditor NewStampEditor(PdfStamp stamp)
    {
        var editor = new StampEditor(stamp, async e =>
        {
            Stamps.Remove(e);
            await SaveStampsAsync();
        });
        editor.PropertyChanged += async (_, _) => await SaveStampsAsync();
        return editor;
    }

    [RelayCommand]
    private async Task AddStampAsync()
    {
        Stamps.Add(NewStampEditor(new PdfStamp(Guid.NewGuid(), T("Neuer Stempel"), Colors[Stamps.Count % Colors.Count])));
        await SaveStampsAsync();
    }

    private async Task SaveStampsAsync()
    {
        if (!_loadingStamps)
        {
            await service.SaveStampsAsync([.. Stamps.Select(s => s.ToStamp())]);
        }
    }
}

/// <summary>
/// A folder in a list, indented under its account and parent folders; account rows are headings that cannot be chosen.
/// <see cref="Path"/> ("info@firma.ch › Posteingang › Rechnungen") is what the closed list shows.
/// </summary>
internal sealed record FolderChoice(string? RemoteId, string Name, string Path, int Depth, bool IsAccount = false)
{
    public bool IsFolder => !IsAccount;

    public Avalonia.Thickness Indent => new(Depth * 16, 0, 0, 0);

    public override string ToString() => Path;
}

internal sealed record QuickActionItem(QuickAction Action, string Summary, string? Warning = null)
{
    public bool HasWarning => Warning is not null;

    public string Label => Action.Label;

    public string Color => Action.Color;
}

internal sealed record EditorOptions(
    IReadOnlyList<Choice<Guid?>> Accounts,
    IReadOnlyDictionary<Guid, IReadOnlyList<FolderChoice>> Folders,
    IReadOnlyList<FolderChoice> AllFolders,
    IReadOnlyList<Choice<Guid?>> Templates,
    IReadOnlyList<Choice<Guid?>> Stamps);

/// <summary>One stamp in the list: text, colour, with name and date.</summary>
internal sealed partial class StampEditor(PdfStamp stamp, Func<StampEditor, Task> remove) : ObservableObject
{
    public Guid Id { get; } = stamp.Id;

    [ObservableProperty]
    public partial string Text { get; set; } = stamp.Text;

    [ObservableProperty]
    public partial string Color { get; set; } = stamp.Color;

    [ObservableProperty]
    public partial bool WithName { get; set; } = stamp.WithName;

    [ObservableProperty]
    public partial bool WithDate { get; set; } = stamp.WithDate;

    public static IReadOnlyList<string> Colors => QuickActionService.Colors;

    [RelayCommand]
    private Task Remove() => remove(this);

    public PdfStamp ToStamp() => new(Id, string.IsNullOrWhiteSpace(Text) ? T("Stempel") : Text.Trim(), Color, WithName, WithDate);
}

/// <summary>Editing one quick action: the button, when it shows, what it does.</summary>
internal sealed partial class QuickActionEditor : ObservableObject
{
    private readonly Guid _id;
    private readonly EditorOptions _options;
    private readonly IFileService _files;
    private readonly Func<QuickAction, Task> _save;
    private readonly Action _cancel;

    public QuickActionEditor(QuickAction action, EditorOptions options, IFileService files, Func<QuickAction, Task> save, Action cancel)
    {
        _id = action.Id;
        _options = options;
        _files = files;
        _save = save;
        _cancel = cancel;
        Label = action.Label;
        Color = action.Color;
        AttachmentKind = AttachmentKinds.FirstOrDefault(k => k.Value == action.When.Attachment) ?? AttachmentKinds[0];
        Extensions = action.When.Extensions ?? string.Empty;
        SenderContains = action.When.SenderContains ?? string.Empty;
        SubjectContains = action.When.SubjectContains ?? string.Empty;
        Account = options.Accounts.FirstOrDefault(a => a.Value == action.When.AccountId) ?? options.Accounts[0];
        Folder = Folders.FirstOrDefault(f => f.RemoteId == action.When.FolderRemoteId) ?? Folders[0];
        foreach (var step in action.Steps)
        {
            Steps.Add(NewStep(step));
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Label { get; set; }

    [ObservableProperty]
    public partial string Color { get; set; }

    public static IReadOnlyList<string> Colors => QuickActionService.Colors;

    // ── When ──

    public static IReadOnlyList<Choice<QuickActionAttachmentKind>> AttachmentKinds { get; } =
    [
        new(QuickActionAttachmentKind.None, T("egal")),
        new(QuickActionAttachmentKind.Any, T("irgendein Anhang")),
        new(QuickActionAttachmentKind.Pdf, T("PDF")),
        new(QuickActionAttachmentKind.Image, T("Bild")),
        new(QuickActionAttachmentKind.Office, T("Office-Datei (Word, Excel …)")),
        new(QuickActionAttachmentKind.Extension, T("Dateiendung …")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsExtensions))]
    public partial Choice<QuickActionAttachmentKind> AttachmentKind { get; set; }

    public bool ShowsExtensions => AttachmentKind.Value == QuickActionAttachmentKind.Extension;

    [ObservableProperty]
    public partial string Extensions { get; set; }

    [ObservableProperty]
    public partial string SenderContains { get; set; }

    [ObservableProperty]
    public partial string SubjectContains { get; set; }

    public IReadOnlyList<Choice<Guid?>> Accounts => _options.Accounts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Folders), nameof(ShowsFolders))]
    public partial Choice<Guid?> Account { get; set; }

    partial void OnAccountChanged(Choice<Guid?> value) => Folder = Folders[0];

    public IReadOnlyList<FolderChoice> Folders =>
        [new(null, T("Alle Ordner"), T("Alle Ordner"), 0), .. Account?.Value is { } id && _options.Folders.TryGetValue(id, out var folders) ? folders : []];

    public bool ShowsFolders => Account?.Value is not null;

    [ObservableProperty]
    public partial FolderChoice? Folder { get; set; }

    // ── Steps ──

    public ObservableCollection<StepEditor> Steps { get; } = [];

    public bool HasSteps => Steps.Count > 0;

    public static IReadOnlyList<Choice<QuickActionStepKind>> StepKinds { get; } =
        [.. Enum.GetValues<QuickActionStepKind>().Select(k => new Choice<QuickActionStepKind>(k, StepEditor.TitleOf(k)))];

    [RelayCommand]
    private void AddStep(Choice<QuickActionStepKind>? kind)
    {
        if (kind is null)
        {
            return;
        }

        Steps.Add(NewStep(new QuickActionStep(kind.Value) { StampId = _options.Stamps.FirstOrDefault()?.Value }));
        OnPropertyChanged(nameof(HasSteps));
    }

    private StepEditor NewStep(QuickActionStep step) => new(step, _options, MoveStep, RemoveStep, _files);

    private void MoveStep(StepEditor step, int by)
    {
        var index = Steps.IndexOf(step);
        if (index + by >= 0 && index + by < Steps.Count)
        {
            Steps.Move(index, index + by);
        }
    }

    private void RemoveStep(StepEditor step)
    {
        Steps.Remove(step);
        OnPropertyChanged(nameof(HasSteps));
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Label);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => _save(new QuickAction(
        _id,
        Label.Trim(),
        Color,
        new QuickActionCondition
        {
            Attachment = AttachmentKind.Value,
            Extensions = ShowsExtensions && !string.IsNullOrWhiteSpace(Extensions) ? Extensions.Trim() : null,
            SenderContains = string.IsNullOrWhiteSpace(SenderContains) ? null : SenderContains.Trim(),
            SubjectContains = string.IsNullOrWhiteSpace(SubjectContains) ? null : SubjectContains.Trim(),
            AccountId = Account.Value,
            FolderRemoteId = Account.Value is null ? null : Folder?.RemoteId,
            FolderName = Account.Value is null || Folder?.RemoteId is null ? null : Folder.Name,
        },
        [.. Steps.Select(s => s.ToStep())]));

    [RelayCommand]
    private void Cancel() => _cancel();
}

/// <summary>One step in the editor; shows only the fields its kind uses.</summary>
internal sealed partial class StepEditor : ObservableObject
{
    private readonly Action<StepEditor, int> _move;
    private readonly Action<StepEditor> _remove;
    private readonly IFileService _files;

    public StepEditor(QuickActionStep step, EditorOptions options, Action<StepEditor, int> move, Action<StepEditor> remove, IFileService files)
    {
        Kind = step.Kind;
        _move = move;
        _remove = remove;
        _files = files;
        Stamps = options.Stamps;
        Templates = options.Templates;
        MoveFolders = options.AllFolders;
        Stamp = Stamps.FirstOrDefault(s => s.Value == step.StampId) ?? Stamps.FirstOrDefault();
        Template = Templates.FirstOrDefault(t => t.Value == step.TemplateId) ?? Templates[0];
        MoveFolder = MoveFolders.FirstOrDefault(f => f.IsFolder && f.RemoteId == step.FolderRemoteId) ?? MoveFolders.FirstOrDefault(f => f.IsFolder && f.Name == step.FolderName);
        Folder = step.Folder ?? string.Empty;
        FileName = step.FileName ?? string.Empty;
        To = step.To ?? string.Empty;
        Subject = step.Subject ?? string.Empty;
    }

    public QuickActionStepKind Kind { get; }

    public string Title => TitleOf(Kind);

    public static string TitleOf(QuickActionStepKind kind) => kind switch
    {
        QuickActionStepKind.StampPdf => T("PDF stempeln"),
        QuickActionStepKind.SaveAttachment => T("Anhang speichern"),
        QuickActionStepKind.Print => T("Anhang drucken"),
        QuickActionStepKind.OpenAttachment => T("Anhang öffnen"),
        QuickActionStepKind.Reply => T("Antworten"),
        QuickActionStepKind.ReplyAll => T("Allen antworten"),
        QuickActionStepKind.Forward => T("Weiterleiten"),
        QuickActionStepKind.NewMail => T("Neue E-Mail mit Anhang"),
        QuickActionStepKind.MoveToFolder => T("In Ordner verschieben"),
        QuickActionStepKind.Flag => T("Kennzeichnen"),
        _ => T("Als gelesen markieren"),
    };

    /// <summary>A line on what the step does (below its title).</summary>
    public string? Hint => Kind switch
    {
        QuickActionStepKind.StampPdf => T("Das PDF öffnet sich in der Vorschau – Sie klicken auf die Stelle für den Stempel."),
        QuickActionStepKind.Print => T("Geht an den Standarddrucker."),
        QuickActionStepKind.Reply or QuickActionStepKind.ReplyAll => T("Mit den gestempelten PDFs als Anhang; die Antwort öffnet sich zum Prüfen und Senden."),
        QuickActionStepKind.Forward => T("Mit allen Anhängen, gestempelte in der neuen Fassung; öffnet sich zum Prüfen und Senden."),
        QuickActionStepKind.NewMail => T("Eine neue E-Mail mit den Anhängen; öffnet sich zum Prüfen und Senden."),
        _ => null,
    };

    public bool ShowsStamp => Kind == QuickActionStepKind.StampPdf;

    public bool ShowsSave => Kind == QuickActionStepKind.SaveAttachment;

    public bool ShowsTemplate => Kind is QuickActionStepKind.Reply or QuickActionStepKind.ReplyAll or QuickActionStepKind.Forward or QuickActionStepKind.NewMail;

    public bool ShowsTo => Kind is QuickActionStepKind.Forward or QuickActionStepKind.NewMail;

    public bool ShowsSubject => Kind == QuickActionStepKind.NewMail;

    public bool ShowsMoveFolder => Kind == QuickActionStepKind.MoveToFolder;

    public IReadOnlyList<Choice<Guid?>> Stamps { get; }

    [ObservableProperty]
    public partial Choice<Guid?>? Stamp { get; set; }

    public IReadOnlyList<Choice<Guid?>> Templates { get; }

    [ObservableProperty]
    public partial Choice<Guid?>? Template { get; set; }

    public IReadOnlyList<FolderChoice> MoveFolders { get; }

    [ObservableProperty]
    public partial FolderChoice? MoveFolder { get; set; }

    // Accounts are headings: choosing one keeps the folder chosen before.
    partial void OnMoveFolderChanged(FolderChoice? oldValue, FolderChoice? newValue)
    {
        if (newValue is { IsAccount: true })
        {
            MoveFolder = oldValue;
        }
    }

    [ObservableProperty]
    public partial string Folder { get; set; }

    [ObservableProperty]
    public partial string FileName { get; set; }

    [ObservableProperty]
    public partial string To { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await _files.PickFolderAsync(T("Ordner zum Speichern")) is { } folder)
        {
            Folder = folder;
        }
    }

    [RelayCommand]
    private void MoveUp() => _move(this, -1);

    [RelayCommand]
    private void MoveDown() => _move(this, 1);

    [RelayCommand]
    private void Remove() => _remove(this);

    public QuickActionStep ToStep() => new(Kind)
    {
        StampId = ShowsStamp ? Stamp?.Value : null,
        Folder = ShowsSave && !string.IsNullOrWhiteSpace(Folder) ? Folder.Trim() : null,
        FileName = ShowsSave && !string.IsNullOrWhiteSpace(FileName) ? FileName.Trim() : null,
        TemplateId = ShowsTemplate ? Template?.Value : null,
        To = ShowsTo && !string.IsNullOrWhiteSpace(To) ? To.Trim() : null,
        Subject = ShowsSubject && !string.IsNullOrWhiteSpace(Subject) ? Subject.Trim() : null,
        FolderRemoteId = ShowsMoveFolder ? MoveFolder?.RemoteId : null,
        FolderName = ShowsMoveFolder ? MoveFolder?.Name : null,
    };
}
