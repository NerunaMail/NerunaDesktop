using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Desktop.Editor;
using Neruna.Desktop.Infrastructure;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Settings → Signaturen: create and edit signatures, and choose per mail account which one is used for new messages
/// and for replies/forwards (default signatures per account).
/// </summary>
internal sealed partial class SignaturesViewModel(SignatureService signatures, IAccountStore accounts, ISettingsStore settings, IFileService files, IWindowService windows) : ViewModelBase
{
    private bool _loading;

    public ObservableCollection<Signature> Signatures { get; } = [];

    /// <summary>"(keine)" plus all signatures, for the assignment dropdowns.</summary>
    public ObservableCollection<SignatureChoice> Choices { get; } = [];

    public ObservableCollection<AccountSignatureRow> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PreviewHtml), nameof(IsCloudSelected))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    public partial Signature? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    /// <summary>A signature of the organisation (Neruna Cloud): shown and usable, but kept in the portal.</summary>
    public bool IsCloudSelected => Selected?.IsFromCloud == true;

    private bool CanModify() => Selected is { IsFromCloud: false };

    public bool HasSignatures => Signatures.Count > 0;

    /// <summary>Preview in the reading-pane renderer, i.e. roughly as recipients see it.</summary>
    public string PreviewHtml => Selected is null ? string.Empty : $"<html><body>{Selected.Html}</body></html>";

    [ObservableProperty]
    public partial string? Error { get; set; }

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            var selectedId = Selected?.Id;
            var all = await signatures.GetAllAsync();
            Signatures.Clear();
            Choices.Clear();
            Choices.Add(SignatureChoice.None);
            foreach (var signature in all)
            {
                Signatures.Add(signature);
                Choices.Add(new SignatureChoice(signature.Id, signature.Name));
            }

            Selected = Signatures.FirstOrDefault(s => s.Id == selectedId) ?? Signatures.FirstOrDefault();
            OnPropertyChanged(nameof(HasSignatures));

            Accounts.Clear();
            foreach (var account in await accounts.GetAccountsAsync())
            {
                if (account.Connections.All(c => c.Kind != ServiceKind.Mail))
                {
                    continue;
                }

                var assignment = await signatures.GetAssignmentAsync(account.Id);
                Accounts.Add(new AccountSignatureRow(account, Choices.ToList(), Find(assignment.NewMessages), Find(assignment.RepliesAndForwards), SaveAssignmentAsync));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        var name = Signatures.Count == 0 ? "Standard" : $"Signatur {Signatures.Count + 1}";
        var saved = await OpenEditorAsync(new Signature(Guid.NewGuid(), name, string.Empty, DateTimeOffset.Now), isNew: true);
        if (saved is not null && Signatures.Count == 1)
        {
            // The first signature becomes the default everywhere.
            foreach (var row in Accounts)
            {
                row.NewMessages = row.RepliesAndForwards = Find(saved.Id);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task EditAsync() => await OpenEditorAsync(Selected!, isNew: false);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteAsync()
    {
        var signature = Selected!;
        await signatures.DeleteAsync(signature.Id);

        // Assignments pointing to it fall back to "(keine)".
        foreach (var row in Accounts)
        {
            if (row.NewMessages?.Id == signature.Id)
            {
                row.NewMessages = SignatureChoice.None;
            }

            if (row.RepliesAndForwards?.Id == signature.Id)
            {
                row.RepliesAndForwards = SignatureChoice.None;
            }
        }

        Selected = null;
        await ReloadAsync();
    }

    private async Task<Signature?> OpenEditorAsync(Signature signature, bool isNew)
    {
        Error = null;
        var editor = new SignatureEditorViewModel(signature, isNew, files, settings);
        var saved = await windows.ShowSignatureEditorAsync(editor);
        if (saved is null)
        {
            return null;
        }

        await signatures.SaveAsync(saved);
        Selected = saved;
        await ReloadAsync();
        return saved;
    }

    private async Task SaveAssignmentAsync(AccountSignatureRow row)
    {
        if (!_loading)
        {
            await signatures.SetAssignmentAsync(row.Account.Id, new SignatureAssignment(row.NewMessages?.Id, row.RepliesAndForwards?.Id));
        }
    }

    private SignatureChoice Find(Guid? id) => Choices.FirstOrDefault(c => c.Id == id) ?? SignatureChoice.None;
}

internal sealed record SignatureChoice(Guid? Id, string Name)
{
    public static SignatureChoice None { get; } = new(null, "(keine)");
}

/// <summary>One mail account with its two default signatures; changes are saved immediately.</summary>
internal sealed partial class AccountSignatureRow : ObservableObject
{
    private readonly Func<AccountSignatureRow, Task> _save;

    public AccountSignatureRow(Account account, IReadOnlyList<SignatureChoice> choices, SignatureChoice newMessages, SignatureChoice replies, Func<AccountSignatureRow, Task> save)
    {
        Account = account;
        Choices = choices;
        _save = save;
        NewMessages = newMessages;
        RepliesAndForwards = replies;
    }

    public Account Account { get; }

    public string Title => Account.Title;

    public IReadOnlyList<SignatureChoice> Choices { get; }

    [ObservableProperty]
    public partial SignatureChoice NewMessages { get; set; }

    [ObservableProperty]
    public partial SignatureChoice RepliesAndForwards { get; set; }

    // A ComboBox may set null while it is torn down; that is not a choice.
    partial void OnNewMessagesChanged(SignatureChoice value)
    {
        if (value is not null)
        {
            _ = _save(this);
        }
    }

    partial void OnRepliesAndForwardsChanged(SignatureChoice value)
    {
        if (value is not null)
        {
            _ = _save(this);
        }
    }
}

/// <summary>Name plus the same HTML editor and toolbar as when writing mails.</summary>
internal sealed partial class SignatureEditorViewModel : ViewModelBase
{
    private readonly Signature _original;
    private readonly ISettingsStore _settings;

    private readonly bool _isTextTemplate;

    /// <param name="isTextTemplate">The same editor for a text template ("Textvorlage"): only its texts differ.</param>
    public SignatureEditorViewModel(Signature original, bool isNew, IFileService files, ISettingsStore settings, bool isTextTemplate = false)
    {
        _isTextTemplate = isTextTemplate;
        _original = original;
        _settings = settings;
        IsNew = isNew;
        Name = original.Name;
        Formatting = new FormattingViewModel(files);
        Formatting.Problem += (_, message) => Error = message;
    }

    /// <summary>Raised with the saved signature, or null when cancelled.</summary>
    public event EventHandler<Signature?>? Finished;

    public bool IsNew { get; }

    public string Title => (_isTextTemplate, IsNew) switch
    {
        (true, true) => "Neue Textvorlage",
        (true, false) => "Textvorlage bearbeiten",
        (false, true) => "Neue Signatur",
        _ => "Signatur bearbeiten",
    };

    public bool IsTextTemplate => _isTextTemplate;

    /// <summary>Text templates: "tel" → typing "tel::" while writing inserts the template.</summary>
    [ObservableProperty]
    public partial string? Shortcut { get; set; }

    /// <summary>Checks the shortcut before saving; returns an error to show, or null when it is fine.</summary>
    public Func<string?, Task<string?>>? ValidateShortcut { get; init; }

    public string NamePlaceholder => _isTextTemplate ? "z. B. Anrufnotiz, Terminbestätigung, Absage" : "z. B. Standard, Kurz, Englisch";

    public FormattingViewModel Formatting { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public async Task AttachEditorAsync(IHtmlEditor editor)
    {
        var font = await _settings.GetAsync(SettingKeys.ComposeFont) is { Length: > 0 } configured ? configured : FontCatalog.DefaultFont;
        var size = double.TryParse(await _settings.GetAsync(SettingKeys.ComposeFontSize), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pt) ? pt : 11;
        Formatting.Attach(editor, _original.Html, font, size, _isTextTemplate
            ? "Text der Vorlage – z. B. eine Tabelle für eine Anrufnotiz (Name, Nummer, Grund) oder eine Standardantwort …"
            : "Signatur hier eingeben – z. B. Name, Funktion, Firma, Telefon, Logo …");
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (ValidateShortcut is not null && await ValidateShortcut(Shortcut) is { } problem)
        {
            Error = problem;
            return;
        }

        var editor = Formatting.Editor;
        var html = editor is null ? _original.Html
            : await editor.GetBodyHtmlAsync() ?? MessageContent.PlainTextToHtml(editor.GetPlainText());
        Finished?.Invoke(this, _original with { Name = Name.Trim(), Html = html, UpdatedAt = DateTimeOffset.Now });
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, null);
}
