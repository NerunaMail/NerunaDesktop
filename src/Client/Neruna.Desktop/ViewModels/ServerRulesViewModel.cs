using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Serverregeln" of an account: rules the mail server applies to incoming mail, also while Neruna is closed (Sieve,
/// Exchange, Microsoft 365). Changed here, stored on the server with "Speichern".
/// </summary>
internal sealed partial class ServerRulesViewModel(MailController mail, Account account, ServiceConnection connection) : ViewModelBase
{
    public event EventHandler? Finished;

    public string Title => F("Serverregeln – {0}", account.Title);

    public ObservableCollection<ServerRuleItem> Rules { get; } = [];

    /// <summary>Rules made elsewhere that Neruna cannot show completely: kept as they are.</summary>
    public ObservableCollection<string> Others { get; } = [];

    public bool HasOthers => Others.Count > 0;

    public IReadOnlyList<FolderChoice> Folders { get; private set; } = [];

    public ServerRuleFeatures Features { get; private set; }

    public IReadOnlyList<Choice<RuleField>> Fields { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoaded { get; set; }

    public bool IsReady => IsLoaded && !IsBusy;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>Changed since loading or saving: "Speichern" stores it on the server.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsDirty { get; set; }

    /// <summary>The rule being edited (new or existing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial ServerRuleEditor? Editor { get; set; }

    public bool IsEditing => Editor is not null;

    public bool IsEmpty => IsLoaded && Rules.Count == 0;

    public async Task LoadAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var folders = await mail.GetFoldersAsync(connection.Id);
            Folders = FolderChoices(folders);
            OnPropertyChanged(nameof(Folders));
            var set = await Task.Run(() => mail.GetServerRulesAsync(connection));
            Features = set.Features;
            Fields = [.. new[] { RuleField.From, RuleField.Recipient, RuleField.Subject, RuleField.Body }
                .Where(f => f != RuleField.Body || Features.HasFlag(ServerRuleFeatures.Body))
                .Select(f => new Choice<RuleField>(f, FieldName(f)))];
            OnPropertyChanged(nameof(Features));
            OnPropertyChanged(nameof(Fields));
            Rules.Clear();
            foreach (var rule in set.Rules)
            {
                Add(rule);
            }

            Others.Clear();
            foreach (var other in set.Others)
            {
                Others.Add(other);
            }

            OnPropertyChanged(nameof(HasOthers));
            IsLoaded = true;
            IsDirty = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex) when (ex is ServerRulesUnavailableException or HttpRequestException or IOException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Add(ServerRule rule, int? at = null)
    {
        var item = new ServerRuleItem(rule, Describe(rule), () => IsDirty = true);
        if (at is { } index)
        {
            Rules[index] = item;
        }
        else
        {
            Rules.Add(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void NewRule()
    {
        Message = null;
        Editor = new ServerRuleEditor(null, new ServerRule(null, string.Empty, true, [new RuleCondition(RuleField.From, string.Empty)]), this);
    }

    [RelayCommand]
    private void Edit(ServerRuleItem item)
    {
        Message = null;
        Editor = new ServerRuleEditor(item, item.Rule, this);
    }

    [RelayCommand]
    private void Remove(ServerRuleItem item)
    {
        Rules.Remove(item);
        IsDirty = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void MoveUp(ServerRuleItem item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(ServerRuleItem item) => Move(item, 1);

    private void Move(ServerRuleItem item, int by)
    {
        var index = Rules.IndexOf(item);
        var to = index + by;
        if (index >= 0 && to >= 0 && to < Rules.Count)
        {
            Rules.Move(index, to);
            IsDirty = true;
        }
    }

    /// <summary>"Übernehmen" in the editor: into the list (stored on the server with "Speichern").</summary>
    internal void Apply(ServerRuleItem? original, ServerRule rule)
    {
        if (original is not null && Rules.IndexOf(original) is var index and >= 0)
        {
            Add(rule, index);
        }
        else
        {
            Add(rule);
        }

        Editor = null;
        IsDirty = true;
    }

    internal void CancelEdit() => Editor = null;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        Error = null;
        Message = null;
        try
        {
            var rules = Rules.Select(r => r.Rule with { IsEnabled = r.IsEnabled }).ToList();
            await Task.Run(() => mail.SaveServerRulesAsync(connection, rules));
            await LoadAsync(); // as the server has them now (with their ids)
            Message = T("Die Regeln sind auf dem Server gespeichert.");
        }
        catch (Exception ex) when (ex is ServerRulesUnavailableException or HttpRequestException or IOException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => Finished?.Invoke(this, EventArgs.Empty);

    internal static string FieldName(RuleField field) => field switch
    {
        RuleField.From => T("Absender enthält"),
        RuleField.Recipient => T("Empfänger (An/Cc) enthält"),
        RuleField.Subject => T("Betreff enthält"),
        _ => T("Text enthält"),
    };

    // "Wenn Absender enthält «x» und Betreff enthält «y»: nach «Rechnungen» verschieben, als gelesen markieren".
    private string Describe(ServerRule rule)
    {
        var conditions = string.Join(T(" und "), rule.Conditions.Select(c => $"{FieldName(c.Field)} «{c.Value}»"));
        var actions = new List<string>();
        if (rule.Delete)
        {
            actions.Add(T("löschen"));
        }
        else if (rule.MoveTo is { } folder)
        {
            actions.Add(F("nach «{0}» verschieben", Folders.FirstOrDefault(f => f.RemoteId == folder)?.Name ?? folder));
        }

        if (rule.MarkRead)
        {
            actions.Add(T("als gelesen markieren"));
        }

        if (rule.Flag)
        {
            actions.Add(T("kennzeichnen"));
        }

        if (!string.IsNullOrWhiteSpace(rule.ForwardTo))
        {
            actions.Add(F("weiterleiten an {0}", rule.ForwardTo));
        }

        if (rule.Stop)
        {
            actions.Add(T("keine weiteren Regeln"));
        }

        return F("Wenn {0}: {1}", conditions, string.Join(", ", actions));
    }

    // As in the folder tree: each folder under its parent.
    private static List<FolderChoice> FolderChoices(IReadOnlyList<MailFolder> folders)
    {
        var result = new List<FolderChoice>();
        var byParent = folders.ToLookup(f => f.ParentRemoteId);
        var known = folders.Select(f => f.RemoteId).ToHashSet(StringComparer.Ordinal);
        void Add(MailFolder folder, int depth)
        {
            result.Add(new FolderChoice(folder.RemoteId, folder.Name, folder.Name, depth));
            foreach (var child in byParent[folder.RemoteId])
            {
                Add(child, depth + 1);
            }
        }

        foreach (var top in folders.Where(f => f.ParentRemoteId is null || !known.Contains(f.ParentRemoteId)))
        {
            Add(top, 0);
        }

        return result;
    }
}

/// <summary>A rule in the list: its name, what it does, on or off.</summary>
internal sealed partial class ServerRuleItem(ServerRule rule, string summary, Action changed) : ObservableObject
{
    public ServerRule Rule { get; } = rule;

    public string Name => string.IsNullOrWhiteSpace(Rule.Name) ? T("Regel ohne Namen") : Rule.Name;

    public string Summary { get; } = summary;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = rule.IsEnabled;

    partial void OnIsEnabledChanged(bool value) => changed();
}

/// <summary>A condition row in the editor.</summary>
internal sealed partial class RuleConditionRow(IReadOnlyList<Choice<RuleField>> fields, RuleCondition condition) : ObservableObject
{
    public IReadOnlyList<Choice<RuleField>> Fields { get; } = fields;

    [ObservableProperty]
    public partial Choice<RuleField>? Field { get; set; } = fields.FirstOrDefault(f => f.Value == condition.Field) ?? fields.FirstOrDefault();

    [ObservableProperty]
    public partial string Value { get; set; } = condition.Value;
}

/// <summary>Editing one rule: name, conditions, actions.</summary>
internal sealed partial class ServerRuleEditor : ObservableObject
{
    private readonly ServerRuleItem? _original;
    private readonly ServerRule _rule;
    private readonly ServerRulesViewModel _owner;

    public ServerRuleEditor(ServerRuleItem? original, ServerRule rule, ServerRulesViewModel owner)
    {
        _original = original;
        _rule = rule;
        _owner = owner;
        Name = rule.Name;
        foreach (var condition in rule.Conditions)
        {
            Conditions.Add(new RuleConditionRow(owner.Fields, condition));
        }

        MoveTo = rule.MoveTo is not null && !rule.Delete;
        Folder = owner.Folders.FirstOrDefault(f => f.RemoteId == rule.MoveTo);
        MarkRead = rule.MarkRead;
        Flag = rule.Flag;
        Forward = !string.IsNullOrWhiteSpace(rule.ForwardTo);
        ForwardTo = rule.ForwardTo ?? string.Empty;
        Delete = rule.Delete;
        Stop = rule.Stop;
    }

    public string Heading => _original is null ? T("Neue Regel") : T("Regel bearbeiten");

    public IReadOnlyList<FolderChoice> Folders => _owner.Folders;

    public bool CanFlag => _owner.Features.HasFlag(ServerRuleFeatures.Flag);

    public bool CanForward => _owner.Features.HasFlag(ServerRuleFeatures.Forward);

    public bool CanDelete => _owner.Features.HasFlag(ServerRuleFeatures.Delete);

    public bool CanStop => _owner.Features.HasFlag(ServerRuleFeatures.Stop);

    [ObservableProperty]
    public partial string Name { get; set; }

    public ObservableCollection<RuleConditionRow> Conditions { get; } = [];

    [ObservableProperty]
    public partial bool MoveTo { get; set; }

    [ObservableProperty]
    public partial FolderChoice? Folder { get; set; }

    [ObservableProperty]
    public partial bool MarkRead { get; set; }

    [ObservableProperty]
    public partial bool Flag { get; set; }

    [ObservableProperty]
    public partial bool Forward { get; set; }

    [ObservableProperty]
    public partial string ForwardTo { get; set; }

    [ObservableProperty]
    public partial bool Delete { get; set; }

    [ObservableProperty]
    public partial bool Stop { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    private void AddCondition() => Conditions.Add(new RuleConditionRow(_owner.Fields, new RuleCondition(RuleField.Subject, string.Empty)));

    [RelayCommand]
    private void RemoveCondition(RuleConditionRow row) => Conditions.Remove(row);

    [RelayCommand]
    private void Apply()
    {
        var conditions = Conditions.Where(c => !string.IsNullOrWhiteSpace(c.Value) && c.Field is not null)
            .Select(c => new RuleCondition(c.Field!.Value, c.Value.Trim())).ToList();
        var rule = _rule with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? string.Join(", ", conditions.Select(c => c.Value)) : Name.Trim(),
            Conditions = conditions,
            MoveTo = MoveTo && !Delete ? Folder?.RemoteId : null,
            MarkRead = MarkRead,
            Flag = Flag && CanFlag,
            ForwardTo = Forward && CanForward && !string.IsNullOrWhiteSpace(ForwardTo) ? ForwardTo.Trim() : null,
            Delete = Delete && CanDelete,
            Stop = Stop && CanStop,
        };
        if (conditions.Count == 0)
        {
            Error = T("Bitte mindestens eine Bedingung mit einem Wert angeben.");
            return;
        }

        if (MoveTo && !Delete && Folder?.RemoteId is null)
        {
            Error = T("Bitte einen Ordner wählen.");
            return;
        }

        if (!rule.HasAction)
        {
            Error = T("Bitte mindestens eine Aktion wählen.");
            return;
        }

        if (rule.ForwardTo is { } to && to.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(a => !MimeKit.MailboxAddress.TryParse(a, out var parsed) || parsed.Address != a || !a.Contains('@', StringComparison.Ordinal)))
        {
            Error = T("Bitte gültige Adressen zum Weiterleiten angeben (mehrere mit Komma trennen).");
            return;
        }

        _owner.Apply(_original, rule);
    }

    [RelayCommand]
    private void Cancel() => _owner.CancelEdit();
}
