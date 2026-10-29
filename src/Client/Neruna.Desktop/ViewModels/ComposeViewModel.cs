using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using MimeKit;
using MimeKit.Utils;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Desktop.Editor;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Writing a mail, inline in the reading pane or in its own window. The body is edited as HTML
/// (<see cref="HtmlEditor"/>); the toolbar here drives it and mirrors the formatting at the caret.
/// </summary>
internal sealed partial class ComposeViewModel : ViewModelBase
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(30);

    private readonly MailController _mail;
    private Account _account;
    private ServiceConnection _connection;
    private readonly IFileService _files;
    private readonly ComposeDraft _draft;
    private readonly SecureMimeService _secureMime;
    private readonly ISettingsStore _settings;
    private readonly SignatureService _signatures;
    private readonly ComposeKind _kind;
    private IHtmlEditor? _editor;
    private bool _autoEncrypt;
    private bool _encryptChosen;
    private bool _settingEncrypt;
    private bool _initialized;
    private int _recipientVersion;

    // The signature in the mail (known: chosen here or placed automatically; unknown in a draft opened again) and
    // whether the user picked it – then a change of recipients no longer swaps it.
    private Signature? _signature;
    private bool _signatureKnown;
    private bool _signatureChosen;
    private int _signatureVersion;
    private ComposeDraft? _editorDraft;

    // Drafts: the saved copy in "Entwürfe", replaced on every save. Edits are counted; a save records the count it
    // captured, so changes typed while saving are not mistaken for saved.
    private readonly string _messageId;
    private readonly DispatcherTimer _autosave;
    private string? _draftRemoteId;
    private int _changes;
    private int _savedChanges;
    private bool _saving;
    private bool _finished;

    public ComposeViewModel(
        MailController mail,
        Account account,
        ServiceConnection connection,
        ComposeDraft draft,
        IFileService files,
        SecureMimeService secureMime,
        ISettingsStore settings,
        SignatureService signatures,
        ComposeKind kind,
        bool encrypt,
        bool isWindow = false,
        IReadOnlyList<(Account Account, ServiceConnection Connection)>? accounts = null)
    {
        _signatures = signatures;
        _kind = kind;
        Formatting = new FormattingViewModel(files);
        Formatting.Problem += (_, message) => Error = message;
        _mail = mail;
        _account = account;
        _connection = connection;
        _files = files;
        _draft = draft;
        _secureMime = secureMime;
        _settings = settings;
        Encrypt = encrypt;
        IsWindow = isWindow;
        // Every address of every mail account; this account's first (its own, then its aliases).
        var others = (accounts ?? []).Where(a => a.Account.Id != account.Id);
        Senders = [.. new[] { (Account: account, Connection: connection) }.Concat(others)
            .SelectMany(a => SendersOf(a.Account, a.Connection).Select(i => new SenderChoice(a.Account, a.Connection, i)))];
        _sender = Senders.FirstOrDefault(c => c.Account.Id == account.Id && string.Equals(c.Identity.Email, draft.From, StringComparison.OrdinalIgnoreCase))
                  ?? Senders.FirstOrDefault(c => c.Account.Id == account.Id)
                  ?? new SenderChoice(account, connection, new MailIdentity(account.EmailAddress ?? string.Empty, account.DisplayName));
        foreach (var choice in Senders)
        {
            choice.ShowAccount = Senders.Any(c => c.Account.Id != choice.Account.Id) && !string.Equals(choice.Identity.Email, choice.Account.Title, StringComparison.OrdinalIgnoreCase);
        }
        To = draft.To;
        Cc = draft.Cc;
        Bcc = draft.Bcc;
        ShowBcc = draft.Bcc.Trim().Length > 0;
        Subject = draft.Subject;
        foreach (var attachment in draft.Attachments)
        {
            Attachments.Add(new ComposeAttachment(MessageContent.FileNameOf(attachment), attachment));
        }

        _draftRemoteId = draft.DraftRemoteId;
        _messageId = draft.MessageId ?? Neruna.Core.Privacy.OutgoingMail.NewMessageId(draft.From);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(To) or nameof(Cc) or nameof(Bcc) or nameof(Subject) or nameof(From))
            {
                MarkChanged();
            }
        };
        Attachments.CollectionChanged += (_, _) =>
        {
            MarkChanged();
            _ = CheckPdfProtectionAsync();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(To) or nameof(Cc) or nameof(Bcc))
            {
                _ = CheckPdfProtectionAsync();
            }
        };

        _autosave = new DispatcherTimer { Interval = AutosaveInterval };
        _autosave.Tick += async (_, _) =>
        {
            if (IsDirty)
            {
                await SaveDraftCoreAsync();
            }
        };
        _autosave.Start();
    }

    /// <summary>The AI bar ("Schreiben", "Verbessern" …); null without AI (tests).</summary>
    public ComposeAiViewModel? Ai { get; set; }

    /// <summary>The editor once attached (the AI reads and replaces text through it).</summary>
    internal IHtmlEditor? Editor => _editor;

    /// <summary>The address books, for completing An/Cc and the contact picker (none in tests).</summary>
    public RecipientDirectory? Recipients { get; init; }

    /// <summary>Toolbar with or without text below the icons (Einstellungen → Design), as in the reading pane.</summary>
    public Neruna.Desktop.Infrastructure.UiPreferences? Preferences { get; init; }

    /// <summary>Adds chosen contacts/groups to An, Cc or Bcc; addresses already in the field are not added twice.</summary>
    public void AddRecipients(RecipientField target, IEnumerable<RecipientEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var field = (target switch { RecipientField.Cc => Cc, RecipientField.Bcc => Bcc, _ => To }) ?? string.Empty;
        var present = InternetAddressList.TryParse(field, out var parsed)
            ? parsed.Mailboxes.Select(m => m.Address).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = entries.SelectMany(e => e.Addresses)
            .Where(a => present.Add(a.Address))
            .Select(RecipientEntry.Format)
            .ToList();
        if (added.Count == 0)
        {
            return;
        }

        var head = field.TrimEnd().TrimEnd(',', ';').TrimEnd();
        var text = (head.Length == 0 ? string.Empty : head + ", ") + string.Join(", ", added);
        switch (target)
        {
            case RecipientField.Cc:
                Cc = text;
                break;
            case RecipientField.Bcc:
                Bcc = text;
                break;
            default:
                To = text;
                break;
        }
    }

    /// <summary>A draft was stored in or removed from "Entwürfe" (the mail page refreshes that folder).</summary>
    public event EventHandler? DraftChanged;

    /// <summary>Messages for the status bar, also after the compose form is gone (saving on leave).</summary>
    public event EventHandler<string>? Notice;

    /// <summary>Identifies the draft across saves (one form per draft).</summary>
    public string MessageId => _messageId;

    /// <summary>Typed since the last saved draft.</summary>
    public bool IsDirty => _changes != _savedChanges;

    /// <summary>Sent, discarded or left: nothing more to save.</summary>
    public bool IsFinished => _finished;

    /// <summary>"Entwurf gespeichert um 10:42" next to the buttons.</summary>
    [ObservableProperty]
    public partial string? DraftStatus { get; set; }

    /// <summary>"Verwerfen" asks once ("Wirklich verwerfen?") when something would be lost.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscardLabel))]
    public partial bool ConfirmDiscard { get; set; }

    public string DiscardLabel => ConfirmDiscard ? T("Wirklich?") : T("Verwerfen");

    /// <summary>Carries unsaved edits over when the draft moves to its own window.</summary>
    public void MarkChanged() => _changes++;

    /// <summary>Raised with true after sending, false when discarded.</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>"Optionen → Übermittlungsbestätigung": the server reports when the mail was delivered (if it can).</summary>
    [ObservableProperty]
    public partial bool RequestDeliveryReceipt { get; set; }

    /// <summary>"Optionen → Lesebestätigung": the recipient's program is asked to confirm reading (the recipient decides).</summary>
    [ObservableProperty]
    public partial bool RequestReadReceipt { get; set; }

    /// <summary>The message went out (its recipients are kept in "Verwendete Adressen").</summary>
    public event EventHandler<MimeMessage>? Sent;

    /// <summary>Runs once the message is sent (a reply or forward marks its original).</summary>
    public Func<Task>? AfterSent { get; set; }

    /// <summary>The user wants this draft in its own window (state is captured with <see cref="CaptureAsync"/>).</summary>
    public event EventHandler? PopOutRequested;

    // Exchange (EWS) sends with the mailbox's primary address whatever is asked: its aliases are no choice.
    private static IEnumerable<MailIdentity> SendersOf(Account account, ServiceConnection connection) =>
        connection.ProviderId == Neruna.Core.Providers.ProviderIds.Ews ? account.Identities.Take(1) : account.Identities;

    /// <summary>The addresses of all mail accounts with their aliases; a choice only appears when there is more than one.</summary>
    public IReadOnlyList<SenderChoice> Senders { get; }

    public bool HasSenderChoice => Senders.Count > 1;

    private SenderChoice _sender;

    /// <summary>
    /// Who sends: an address of this or another account (a reply starts with the address it was sent to). Another
    /// account sends through its own server; the saved draft moves to its "Entwürfe", its signature replaces the one here.
    /// </summary>
    public SenderChoice Sender
    {
        get => _sender;
        set
        {
            if (value is null || value == _sender)
            {
                return;
            }

            var previous = _sender;
            _sender = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(From));
            OnPropertyChanged(nameof(FromText));
            MarkChanged();
            _ = RefreshSignAsync();
            if (value.Account.Id != previous.Account.Id)
            {
                _account = value.Account;
                _connection = value.Connection;
                OnPropertyChanged(nameof(Account));
                OnPropertyChanged(nameof(Connection));
                _ = SwitchAccountAsync(previous.Connection);
            }
        }
    }

    /// <summary>The sender's address and name.</summary>
    public MailIdentity From => _sender.Identity;

    // The draft saved so far lies in the other account's "Entwürfe": removed there, saved anew with this account.
    private async Task SwitchAccountAsync(ServiceConnection previous)
    {
        while (_saving)
        {
            await Task.Delay(50);
        }

        if (_draftRemoteId is { } id)
        {
            _draftRemoteId = null;
            try
            {
                await _mail.DeleteDraftAsync(previous, id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Notice?.Invoke(this, T("Der Entwurf konnte nicht aus «Entwürfe» entfernt werden: ") + ex.Message);
            }
        }

        if (_editor is not null)
        {
            try
            {
                _signatureChosen = false;
                await PlaceSignatureAsync(await _signatures.ResolveAsync(_account.Id, _kind, ToAddresses()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Notice?.Invoke(this, ex.Message);
            }
        }
    }

    private async Task RefreshSignAsync()
    {
        CanSign = (await _secureMime.GetCapabilitiesAsync(From.Email, [])).CanSign;
        if (!CanSign)
        {
            Sign = false;
        }
    }

    public string FromText => From.ToString();

    public Account Account => _account;

    public ServiceConnection Connection => _connection;

    public bool IsWindow { get; }

    public bool CanPopOut => !IsWindow;

    public string WindowTitle => string.IsNullOrWhiteSpace(Subject) ? T("Neue E-Mail") : Subject;

    public ObservableCollection<ComposeAttachment> Attachments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string To { get; set; }

    [ObservableProperty]
    public partial string Cc { get; set; }

    /// <summary>Blind copies: sent, but not shown to the other recipients (kept in the own sent copy and draft).</summary>
    [ObservableProperty]
    public partial string Bcc { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowBcc { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string Subject { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSending { get; set; }

    /// <summary>S/MIME signature; only possible with an own valid certificate for the sender address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEncrypt))]
    public partial bool Sign { get; set; }

    [ObservableProperty]
    public partial bool Encrypt { get; set; }

    /// <summary>An own S/MIME certificate for the sender exists – only then are Signieren and Verschlüsseln offered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEncrypt))]
    public partial bool CanSign { get; set; }

    /// <summary>
    /// Verschlüsseln only together with Signieren: without an own certificate the copy in "Gesendet" could not be read
    /// (shown while switched on, so it can be switched off).
    /// </summary>
    public bool ShowEncrypt => Sign || Encrypt;

    // Signing off: encrypting goes off too; signing on again: encryption is checked again for the recipients.
    partial void OnSignChanged(bool value)
    {
        if (!value && Encrypt)
        {
            SetEncryptAutomatically(false);
        }
        else if (value)
        {
            ScheduleEncryptionCheck();
        }
    }

    /// <summary>Tooltip of "Verschlüsseln": why it was switched on or off automatically.</summary>
    [ObservableProperty]
    public partial string EncryptHint { get; set; } = T("Mit S/MIME verschlüsseln (Zertifikate aller Empfänger nötig)");

    public string SignHint => CanSign ? T("Digital signieren (S/MIME)") : T("Kein eigenes S/MIME-Zertifikat für diese Adresse (Einstellungen → Zertifikate)");

    /// <summary>The formatting toolbar, connected to the editor in <see cref="AttachEditorAsync"/>.</summary>
    public FormattingViewModel Formatting { get; }

    /// <summary>The text templates (own and the organisation's); none when composing without them (tests).</summary>
    public TextTemplateService? TextTemplates { get; init; }

    /// <summary>The "Textvorlage" menu: inserted at the caret, nothing replaced.</summary>
    public ObservableCollection<TextTemplateMenuItem> TemplateMenu { get; } = [];

    public bool HasTemplates => TemplateMenu.Count > 0;

    /// <summary>The "Signatur" menu: every signature plus "Keine Signatur".</summary>
    public ObservableCollection<SignatureMenuItem> SignatureMenu { get; } = [];

    partial void OnCanSignChanged(bool value) => OnPropertyChanged(nameof(SignHint));

    // Once the user switches encryption themselves, recipients no longer change it.
    partial void OnEncryptChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEncrypt));
        if (!_settingEncrypt)
        {
            _encryptChosen = true;
        }
    }

    partial void OnToChanged(string value)
    {
        ScheduleEncryptionCheck();
        RefreshTemplateRecipient();
        _ = FollowRecipientSignatureAsync();
    }

    /// <summary>The addresses in «An», as far as they can be read yet.</summary>
    private List<string> ToAddresses() =>
        InternetAddressList.TryParse(To ?? string.Empty, out var list) ? [.. list.Mailboxes.Select(m => m.Address)] : [];

    /// <summary>
    /// «An» changed: the signature last used for these recipients (e.g. «Du»), as long as the user has not picked one
    /// in this mail.
    /// </summary>
    private async Task FollowRecipientSignatureAsync()
    {
        if (_editor is null || _signatureChosen || !_signatureKnown)
        {
            return;
        }

        var version = ++_signatureVersion;
        try
        {
            var signature = await _signatures.ResolveAsync(_account.Id, _kind, ToAddresses());
            if (version == _signatureVersion && !_signatureChosen && signature?.Id != _signature?.Id)
            {
                await PlaceSignatureAsync(signature);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke(this, ex.Message);
        }
    }

    /// <summary>Puts <paramref name="signature"/> in its place in the text (automatically; not the user's choice).</summary>
    private Task PlaceSignatureAsync(Signature? signature)
    {
        _signature = signature;
        _signatureKnown = true;
        return Formatting.RunAsync($"neruna.setSignature({FormattingViewModel.Js(signature?.Html ?? string.Empty)})");
    }

    partial void OnCcChanged(string value) => ScheduleEncryptionCheck();

    private void ScheduleEncryptionCheck()
    {
        if (!_initialized)
        {
            return;
        }

        // Checked shortly after typing stops; an older check never overwrites a newer one.
        var version = ++_recipientVersion;
        _ = CheckLaterAsync();

        async Task CheckLaterAsync()
        {
            await Task.Delay(400);
            await UpdateEncryptionAsync(version);
        }
    }

    private async Task UpdateEncryptionAsync(int version)
    {
        if (!_autoEncrypt || _encryptChosen || version != _recipientVersion)
        {
            return;
        }

        if (!Sign)
        {
            SetEncryptAutomatically(false);
            return;
        }

        var recipients = new[] { To, Cc, Bcc }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .SelectMany(text => InternetAddressList.TryParse(text, out var list) ? list.Mailboxes.Select(m => m.Address) : [])
            .ToList();
        if (recipients.Count == 0)
        {
            SetEncryptAutomatically(false);
            EncryptHint = T("Wird automatisch eingeschaltet, sobald für alle Empfänger ein Zertifikat bekannt ist");
            return;
        }

        // The sender is always encrypted to as well (readable copy in "Gesendet").
        var sender = From.Email;
        var capabilities = await _secureMime.GetCapabilitiesAsync(sender, recipients.Append(sender));
        if (version != _recipientVersion || _encryptChosen)
        {
            return;
        }

        var missing = capabilities.RecipientsWithoutCertificate;
        SetEncryptAutomatically(missing.Count == 0);
        EncryptHint = missing.Count == 0
            ? T("Automatisch verschlüsselt: Zertifikate aller Empfänger sind bekannt")
            : T("Nicht automatisch verschlüsselt – kein Zertifikat für: ") + string.Join(", ", missing);
    }

    private void SetEncryptAutomatically(bool value)
    {
        _settingEncrypt = true;
        try
        {
            Encrypt = value;
        }
        finally
        {
            _settingEncrypt = false;
        }
    }

    /// <summary>
    /// Signs automatically when the sender has a certificate, and encrypts automatically while certificates of all
    /// recipients are known (both can be switched off in the settings). A popped-out draft keeps its own choice.
    /// </summary>
    public async Task InitializeAsync(bool? sign = null, bool? encrypt = null)
    {
        CanSign = (await _secureMime.GetCapabilitiesAsync(From.Email, [])).CanSign;
        Sign = CanSign && (sign ?? await _settings.GetBoolAsync(SettingKeys.AutoSign, fallback: true));

        // Replies to encrypted mail stay encrypted; otherwise the recipients decide.
        _encryptChosen = encrypt is not null || Encrypt;
        SetEncryptAutomatically(encrypt ?? Encrypt);
        _autoEncrypt = await _settings.GetBoolAsync(SettingKeys.AutoEncrypt, fallback: true);
        _initialized = true;
        await UpdateEncryptionAsync(_recipientVersion);
        foreach (var signature in await _signatures.GetAllAsync())
        {
            SignatureMenu.Add(new SignatureMenuItem(signature.Name, signature, ChooseSignatureCommand));
        }

        SignatureMenu.Add(new SignatureMenuItem(T("Keine Signatur"), null, ChooseSignatureCommand));
    }

    public ComposeKind Kind => _kind;

    /// <summary>Called by the view once its editor exists; loads the draft (with signature) in the user's default font.</summary>
    public async Task AttachEditorAsync(IHtmlEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        editor.SendRequested += (_, _) =>
        {
            if (SendCommand.CanExecute(null))
            {
                _ = SendCommand.ExecuteAsync(null);
            }
        };
        editor.SaveRequested += (_, _) => _ = SaveDraftCommand.ExecuteAsync(null);
        editor.ContentChanged += (_, _) => MarkChanged();

        var font = await _settings.GetAsync(SettingKeys.ComposeFont) is { Length: > 0 } configured ? configured : FontCatalog.DefaultFont;
        var size = double.TryParse(await _settings.GetAsync(SettingKeys.ComposeFontSize), NumberStyles.Float, CultureInfo.InvariantCulture, out var pt) ? pt : 11;

        // A popped-out or saved draft already carries its signature (which one is not known: left as it is). Otherwise
        // the one last used for these recipients, else the account's default.
        ComposeDraft draft;
        if (_draft.HtmlBody?.Contains(SignatureBlock.ElementId, StringComparison.Ordinal) == true || _draft.DraftRemoteId is not null)
        {
            draft = _draft;
        }
        else
        {
            _signature = await _signatures.ResolveAsync(_account.Id, _kind, ToAddresses());
            _signatureKnown = true;
            draft = SignatureBlock.Apply(_draft, _signature);
        }

        var html = draft.HtmlBody ?? (string.IsNullOrEmpty(draft.Body) ? string.Empty : PlainToHtml(draft.Body));
        Formatting.Attach(editor, html, font, size, T("Nachricht verfassen …"));
        editor.SetAutoLists(await _settings.GetBoolAsync(SettingKeys.ComposeAutoLists, fallback: true));
        editor.SetParagraphSpacing(await _settings.GetParagraphSpacingAsync());
        _editorDraft = draft;
        await LoadTemplatesAsync(editor);
    }

    /// <summary>
    /// Everything typed so far, to continue in another window. This form is finished afterwards: the window takes over
    /// the draft (and its saved copy).
    /// </summary>
    public async Task<(ComposeDraft Draft, bool Sign, bool Encrypt)> CaptureAsync()
    {
        Finish();
        while (_saving)
        {
            await Task.Delay(50);
        }

        return (await SnapshotAsync(), Sign, Encrypt);
    }

    // The current state as a draft: recipients, subject, text (HTML or plain), attachments, identity of the saved copy.
    private async Task<ComposeDraft> SnapshotAsync()
    {
        string? html = null;
        var body = string.Empty;
        if (_editor is { IsPlainText: true })
        {
            body = _editor.GetPlainText();
        }
        else
        {
            // Before the editor has loaded, the prepared content is still what the user sees.
            html = (_editor is null ? null : await _editor.GetHtmlAsync()) ?? (_editorDraft ?? _draft).HtmlBody;
            body = html is null ? (_editorDraft ?? _draft).Body : string.Empty;
        }

        return _draft with
        {
            From = From.Email,
            To = To,
            Cc = Cc,
            Bcc = Bcc,
            Subject = Subject,
            HtmlBody = html,
            Body = body,
            Attachments = Attachments.Select(a => a.Entity).ToList(),
            DraftRemoteId = _draftRemoteId,
            MessageId = _messageId,
        };
    }

    [RelayCommand]
    private Task SaveDraftAsync() => SaveDraftCoreAsync();

    private async Task SaveDraftCoreAsync()
    {
        if (_saving || _finished)
        {
            return;
        }

        _saving = true;
        try
        {
            var version = _changes;
            await StoreDraftAsync(await SnapshotAsync(), version);
            DraftStatus = F("Entwurf gespeichert um {0:t}", DateTime.Now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DraftStatus = T("Entwurf nicht gespeichert: ") + ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }

    // Drafts are stored as plain MIME without S/MIME; signing and encryption happen only when sending.
    private async Task StoreDraftAsync(ComposeDraft draft, int version)
    {
        var message = MessageComposer.Build(SenderAddress, draft, [], forDraft: true);
        _draftRemoteId = await _mail.SaveDraftAsync(_connection, message, _draftRemoteId);
        _savedChanges = version;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The form is going away (another message opened, window closed): takes the text right away – the editor may be
    /// gone a moment later – and stores it in "Entwürfe" in the background, if anything changed.
    /// </summary>
    public async Task SaveOnLeaveAsync()
    {
        if (_finished)
        {
            return;
        }

        Finish();
        if (!IsDirty)
        {
            return;
        }

        var version = _changes;
        var draft = await SnapshotAsync();
        _ = StoreInBackgroundAsync(draft, version);
    }

    private async Task StoreInBackgroundAsync(ComposeDraft draft, int version)
    {
        while (_saving)
        {
            await Task.Delay(100);
        }

        try
        {
            await StoreDraftAsync(draft, version);
            Notice?.Invoke(this, T("Entwurf gespeichert (Ordner «Entwürfe»)."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke(this, T("Entwurf konnte nicht gespeichert werden: ") + ex.Message);
        }
    }

    /// <summary>
    /// Closing Neruna with this draft open, "Speichern": stores it now and waits until it is on the server.
    /// </summary>
    /// <returns>False if it could not be stored (the reason is in <see cref="Error"/>).</returns>
    public async Task<bool> SaveBeforeExitAsync()
    {
        if (_finished)
        {
            return true;
        }

        Finish();
        while (_saving)
        {
            await Task.Delay(50);
        }

        if (!IsDirty)
        {
            return true;
        }

        try
        {
            await StoreDraftAsync(await SnapshotAsync(), _changes);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Entwurf nicht gespeichert: ") + ex.Message;
            _finished = false;
            return false;
        }
    }

    /// <summary>Closing Neruna with this draft open, "Nicht speichern": the latest changes are dropped.</summary>
    public void DiscardOnExit() => Finish();

    /// <summary>"Offerte Netzwerk" or "(ohne Betreff)" for the question on closing.</summary>
    public string Title => string.IsNullOrWhiteSpace(Subject) ? T("(ohne Betreff)") : Subject.Trim();

    private void Finish()
    {
        _finished = true;
        _autosave.Stop();
    }

    // Once sent or discarded, the saved copy in "Entwürfe" is not needed any more.
    private async Task RemoveSavedDraftAsync()
    {
        while (_saving)
        {
            await Task.Delay(50);
        }

        if (_draftRemoteId is not { } id)
        {
            return;
        }

        try
        {
            await _mail.DeleteDraftAsync(_connection, id);
            _draftRemoteId = null;
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke(this, T("Der Entwurf konnte nicht aus «Entwürfe» entfernt werden: ") + ex.Message);
        }
    }

    private MailboxAddress SenderAddress =>
        new(From.DisplayName, string.IsNullOrEmpty(From.Email) ? throw new InvalidOperationException("Account has no email address.") : From.Email);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        Error = null;
        if (!InternetAddressList.TryParse(To, out _) || (Cc.Trim().Length > 0 && !InternetAddressList.TryParse(Cc, out _))
            || (Bcc.Trim().Length > 0 && !InternetAddressList.TryParse(Bcc, out _)))
        {
            Error = T("Bitte gültige Empfängeradressen eingeben (mehrere mit Komma trennen).");
            return;
        }

        IsSending = true;
        try
        {
            if (!await ProtectPdfsForSendingAsync())
            {
                return;
            }

            var message = MessageComposer.Build(SenderAddress, await SnapshotAsync(), []);
            Receipts.Request(message, RequestDeliveryReceipt, RequestReadReceipt);
            await _secureMime.ProtectAsync(message, Sign, Encrypt);
            await _mail.SendAsync(_connection, message);
            Sent?.Invoke(this, message);
            await RememberSignatureAsync();
            if (AfterSent is { } afterSent)
            {
                await afterSent();
            }

            Finish();
            await RemoveSavedDraftAsync();
            Closed?.Invoke(this, true);
        }
        catch (SecureMimeException ex)
        {
            Error = ex.Message + (Encrypt ? T(" – ohne Verschlüsselung senden: Schalter «Verschlüsseln» ausschalten.") : string.Empty);
        }
        catch (Exception ex)
        {
            Error = T("Senden fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    private bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(To);

    // Sent: the next mail to these recipients gets the same signature (only a deviation from the default is kept).
    private async Task RememberSignatureAsync()
    {
        if (!_signatureKnown)
        {
            return;
        }

        try
        {
            await _signatures.RememberAsync(_account.Id, _kind, ToAddresses(), _signature);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The mail is sent; only the choice is not kept.
            Notice?.Invoke(this, ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddAttachmentAsync() => await AttachFilesAsync(await _files.PickFilesAsync(T("Datei anhängen")));

    /// <summary>Files as attachments (picked, or dragged into the mail); folders cannot be attached.</summary>
    public async Task AttachFilesAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                Error = F("«{0}» ist ein Ordner – Ordner lassen sich nur als Link einfügen (beim Ziehen Ctrl drücken).", Path.GetFileName(path.TrimEnd('/', '\\')));
                continue;
            }

            try
            {
                var entity = await new BodyBuilder().Attachments.AddAsync(path);
                Attachments.Add(new ComposeAttachment(Path.GetFileName(path), entity));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = F("«{0}» konnte nicht gelesen werden: {1}", Path.GetFileName(path), ex.Message);
            }
        }
    }

    /// <summary>A dropped file known only by its content (no path): attached as is.</summary>
    public void AttachData(string name, byte[]? data, bool tooLarge)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (data is null)
        {
            Error = tooLarge ? F("«{0}» ist zu gross zum Hineinziehen – bitte über «Anhängen» wählen.", name) : F("«{0}» konnte nicht gelesen werden.", name);
            return;
        }

        var entity = new BodyBuilder().Attachments.Add(name, data);
        Attachments.Add(new ComposeAttachment(name, entity));
    }

    /// <summary>Messages dragged in from the mail list: attached as .eml, as with "Als Anhang weiterleiten".</summary>
    public async Task AttachMessagesAsync(Func<Task<IReadOnlyList<MimeMessage>>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        IReadOnlyList<MimeMessage> messages;
        try
        {
            messages = await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Nachrichten konnten nicht geladen werden") + ": " + ex.Message;
            return;
        }

        foreach (var message in messages)
        {
            var part = new MessagePart { Message = message, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) };
            part.ContentDisposition.FileName = MessageContent.FileNameOf(part);
            Attachments.Add(new ComposeAttachment(part.ContentDisposition.FileName, part));
        }

        if (messages.Count > 0)
        {
            MarkChanged();
        }
    }

    /// <summary>
    /// Links to the files where they were dropped: the path as text, so it reads the same in every mail program
    /// (in Neruna it is only clickable for recipients who set the sender as safe).
    /// </summary>
    public async Task InsertFileLinksAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (_editor is null || paths.Count == 0)
        {
            return;
        }

        var html = string.Join("<br>", paths.Select(p => $"<a href=\"{System.Net.WebUtility.HtmlEncode(FileLinks.ToUri(p))}\">{System.Net.WebUtility.HtmlEncode(p)}</a>"));
        var text = string.Join(Environment.NewLine, paths.Select(p => "<" + FileLinks.ToUri(p) + ">"));
        await _editor.InsertAtDropAsync(html, text);
        MarkChanged();
    }

    [RelayCommand]
    private void RemoveAttachment(ComposeAttachment attachment) => Attachments.Remove(attachment);

    [RelayCommand]
    private async Task DiscardAsync()
    {
        // Asks once when typed text or a saved draft would be lost.
        if (!ConfirmDiscard && (IsDirty || _draftRemoteId is not null))
        {
            ConfirmDiscard = true;
            return;
        }

        Finish();
        await RemoveSavedDraftAsync();
        Closed?.Invoke(this, false);
    }

    [RelayCommand]
    private void PopOut() => PopOutRequested?.Invoke(this, EventArgs.Empty);

    // Menu and "kürzel::" shortcuts of the text templates.
    private async Task LoadTemplatesAsync(IHtmlEditor editor)
    {
        if (TextTemplates is null)
        {
            return;
        }

        TemplateMenu.Clear();
        foreach (var template in await TextTemplates.GetAllAsync())
        {
            var title = template.Shortcut is { } key ? $"{template.Name}   ({key}::)" : template.Name;
            TemplateMenu.Add(new TextTemplateMenuItem(title, template, InsertTemplateCommand));
        }

        OnPropertyChanged(nameof(HasTemplates));
        _shortcutTemplates = await TextTemplates.GetShortcutsAsync();
        await PushShortcutsAsync(editor);
    }

    private IReadOnlyDictionary<string, TextTemplate> _shortcutTemplates = new Dictionary<string, TextTemplate>();
    private int _shortcutVersion;

    // The editor replaces "kürzel::" itself, so it gets the templates already filled for the current recipient – again
    // whenever "An" changes (see OnToChanged).
    private async Task PushShortcutsAsync(IHtmlEditor editor)
    {
        var recipient = _shortcutTemplates.Values.Any(t => TemplateFields.HasFields(t.Html)) ? await TemplateRecipientAsync() : null;
        editor.SetShortcuts(_shortcutTemplates.ToDictionary(
            p => p.Key, p => FilledTemplate(p.Value, recipient), StringComparer.OrdinalIgnoreCase));
    }

    private static (string Html, string Text) FilledTemplate(TextTemplate template, TemplateRecipient? recipient)
    {
        var html = TemplateFields.Fill(template.Html, recipient);
        return (html, HtmlText.ToPlainText(html));
    }

    /// <summary>Who {{recipient_…}} in text templates stands for: the first address in "An", with its contact.</summary>
    private Task<TemplateRecipient?> TemplateRecipientAsync() =>
        TemplateFields.ResolveAsync(To, email => Recipients?.FindContactAsync(email) ?? Task.FromResult<Neruna.Core.Contacts.ContactCard?>(null));

    private void RefreshTemplateRecipient()
    {
        if (_editor is not { } editor || !_shortcutTemplates.Values.Any(t => TemplateFields.HasFields(t.Html)))
        {
            return;
        }

        _ = RefreshLaterAsync(editor, ++_shortcutVersion);
    }

    // Once typing in "An" pauses; a later change wins.
    private async Task RefreshLaterAsync(IHtmlEditor editor, int version)
    {
        await Task.Delay(400);
        if (version == _shortcutVersion)
        {
            await PushShortcutsAsync(editor);
        }
    }

    [RelayCommand]
    private async Task InsertTemplate(TextTemplate? template)
    {
        if (template is null || _editor is null)
        {
            return;
        }

        MarkChanged();
        var (html, text) = FilledTemplate(template, TemplateFields.HasFields(template.Html) ? await TemplateRecipientAsync() : null);
        await _editor.InsertAsync(html, text);
    }

    /// <summary>Picked in the menu: stays, whatever the recipients – and is remembered for them when sending.</summary>
    [RelayCommand]
    private Task ChooseSignature(Signature? signature)
    {
        MarkChanged();
        _signatureChosen = true;
        return PlaceSignatureAsync(signature);
    }

    private static string PlainToHtml(string text) =>
        string.Join("", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => "<div>" + (line.Length == 0 ? "<br>" : WebUtility.HtmlEncode(line)) + "</div>"));
}

/// <summary>An attachment of the mail being written; PDFs know whether they need a password to open.</summary>
internal sealed partial class ComposeAttachment(string fileName, MimeEntity entity) : ObservableObject
{
    public string FileName { get; } = fileName;

    public MimeEntity Entity { get; } = entity;

    public bool IsPdf { get; } = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || entity.ContentType.IsMimeType("application", "pdf");

    /// <summary>Opens only with a password (checked when attached).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProtect))]
    public partial bool IsProtected { get; set; }

    /// <summary>Settings → PDF: "Mit Kennwort schützen" is offered (on unless switched off).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProtect))]
    public partial bool OffersProtection { get; set; }

    public bool CanProtect => OffersProtection && IsPdf && !IsProtected;

    /// <summary>Whether <see cref="IsProtected"/> was looked up already.</summary>
    public bool IsChecked { get; set; }
}

/// <summary>An address to send from: of which account (through its server), with which name.</summary>
internal sealed class SenderChoice(Account account, ServiceConnection connection, MailIdentity identity)
{
    public Account Account { get; } = account;

    public ServiceConnection Connection { get; } = connection;

    public MailIdentity Identity { get; } = identity;

    /// <summary>With several accounts, the account's name too where the address alone does not say it.</summary>
    public bool ShowAccount { get; set; }

    public override string ToString() => ShowAccount ? $"{Identity} – {Account.Title}" : Identity.ToString();
}

internal sealed record SignatureMenuItem(string Title, Signature? Signature, System.Windows.Input.ICommand Command);

internal sealed record TextTemplateMenuItem(string Title, TextTemplate Template, System.Windows.Input.ICommand Command);

/// <summary>The recipient fields of the compose form.</summary>
internal enum RecipientField
{
    To,
    Cc,
    Bcc,
}
