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
            Actions.Add(new QuickActionItem(action, Describe(action)));
        }

        OnPropertyChanged(nameof(HasActions));
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
        var folders = new Dictionary<Guid, IReadOnlyList<Choice<string?>>>();
        foreach (var account in await accounts.GetAccountsAsync())
        {
            if (account.Connections.FirstOrDefault(c => c.Kind == ServiceKind.Mail) is not { } connection)
            {
                continue;
            }

            accountChoices.Add(new Choice<Guid?>(account.Id, account.Title));
            folders[account.Id] = [.. (await mail.GetFoldersAsync(connection.Id))
                .OrderBy(f => f.Role == FolderRole.None ? 1 : 0).ThenBy(f => f.Role).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new Choice<string?>(f.RemoteId, MailFolderNode.DisplayName(f)))];
        }

        var templateChoices = new List<Choice<Guid?>> { new(null, T("Keine")) };
        templateChoices.AddRange((await templates.GetAllAsync()).Select(t => new Choice<Guid?>(t.Id, t.Name)));
        var stampChoices = Stamps.Select(s => new Choice<Guid?>(s.Id, s.Text)).ToList();
        return new EditorOptions(accountChoices, folders, templateChoices, stampChoices);
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

internal sealed record QuickActionItem(QuickAction Action, string Summary)
{
    public string Label => Action.Label;

    public string Color => Action.Color;
}

internal sealed record EditorOptions(
    IReadOnlyList<Choice<Guid?>> Accounts,
    IReadOnlyDictionary<Guid, IReadOnlyList<Choice<string?>>> Folders,
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
        Folder = Folders.FirstOrDefault(f => f.Value == action.When.FolderRemoteId) ?? Folders[0];
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

    public IReadOnlyList<Choice<string?>> Folders =>
        [new(null, T("Alle Ordner")), .. Account?.Value is { } id && _options.Folders.TryGetValue(id, out var folders) ? folders : []];

    public bool ShowsFolders => Account?.Value is not null;

    [ObservableProperty]
    public partial Choice<string?>? Folder { get; set; }

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
            FolderRemoteId = Account.Value is null ? null : Folder?.Value,
            FolderName = Account.Value is null || Folder?.Value is null ? null : Folder.Title,
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
        MoveFolders = [.. options.Folders.Values.SelectMany(f => f).GroupBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First())];
        Stamp = Stamps.FirstOrDefault(s => s.Value == step.StampId) ?? Stamps.FirstOrDefault();
        Template = Templates.FirstOrDefault(t => t.Value == step.TemplateId) ?? Templates[0];
        MoveFolder = MoveFolders.FirstOrDefault(f => f.Value == step.FolderRemoteId) ?? MoveFolders.FirstOrDefault(f => f.Title == step.FolderName);
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

    public IReadOnlyList<Choice<string?>> MoveFolders { get; }

    [ObservableProperty]
    public partial Choice<string?>? MoveFolder { get; set; }

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
        FolderRemoteId = ShowsMoveFolder ? MoveFolder?.Value : null,
        FolderName = ShowsMoveFolder ? MoveFolder?.Title : null,
    };
}
