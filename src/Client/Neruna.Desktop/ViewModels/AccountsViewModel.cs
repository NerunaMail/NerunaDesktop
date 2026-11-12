using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Account list, removal and diagnostics (where data and logs live).</summary>
internal sealed partial class AccountsViewModel(IAccountStore accounts, AccountSetupService setup, ICredentialStore credentials, IFileService files, AppOptions options,
    Neruna.Core.Cloud.AccountTransferService transfer,
    Neruna.Core.Cloud.CloudController cloud,
    Neruna.Core.Mail.MailController mail,
    Neruna.Core.Auth.IBrowserLauncher browser,
    Neruna.Core.ISettingsStore settings) : ViewModelBase
{
    /// <summary>Webmail, instructions or support of an account's provider, in the browser.</summary>
    [RelayCommand]
    private async Task OpenLinkAsync(LinkItem link)
    {
        try
        {
            await browser.OpenAsync(link.Url);
        }
        catch (Neruna.Core.Auth.OAuthException)
        {
            // no browser: nothing else to do here
        }
    }

    /// <summary>"Aufs Handy übertragen" for an account: the shell shows the dialog (computer login, QR code, PIN).</summary>
    public event EventHandler<AccountTransferDialogViewModel>? TransferRequested;

    [RelayCommand]
    private void Transfer(AccountItem item) => TransferRequested?.Invoke(this, new AccountTransferDialogViewModel(transfer, item.Account));

    public event EventHandler? AddAccountRequested;

    public event EventHandler? SubscribeRequested;

    /// <summary>"Geburtstage oder Feiertage …" under Zusatzkalender.</summary>
    public event EventHandler? SpecialRequested;

    /// <summary>"⋯ → Serverregeln …" of an account.</summary>
    public event EventHandler<Account>? ServerRulesRequested;

    [RelayCommand]
    private void ServerRules(AccountItem item) => ServerRulesRequested?.Invoke(this, item.Account);

    /// <summary>"Outlook-Datendatei (.pst) einbinden …".</summary>
    public event EventHandler? PstRequested;

    /// <summary>"Bearbeiten": the shell shows the setup dialog with this account.</summary>
    public event EventHandler<Neruna.Core.Accounts.Account>? EditRequested;

    /// <summary>"Abwesenheit" on an account with mail.</summary>
    public event EventHandler<Neruna.Core.Accounts.Account>? AutoReplyRequested;

    /// <summary>The colors as stored (also after one was chosen in the folder list).</summary>
    public async Task RefreshColorsAsync()
    {
        foreach (var item in Items)
        {
            await ShowColorAsync(item);
        }
    }

    private async Task ShowColorAsync(AccountItem item)
    {
        var accountId = item.Account.Id;
        item.SetColor(await AccountColors.GetAsync(settings, accountId), async color =>
        {
            await AccountColors.SetAsync(settings, accountId, color);
            ColorsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>An account got another color: the folder list takes it.</summary>
    public event EventHandler? ColorsChanged;

    /// <summary>Raised after an account was removed, so the other pages reload.</summary>
    public event EventHandler? AccountsChanged;

    public ObservableCollection<AccountItem> Items { get; } = [];

    /// <summary>Settings → Konten: the real accounts (mail, calendar, contacts, data files).</summary>
    public ObservableCollection<AccountItem> MainItems { get; } = [];

    /// <summary>
    /// Settings → Kalender → Zusatzkalender: birthdays, holidays and subscribed internet calendars – no account to sign
    /// in to, so not among the accounts.
    /// </summary>
    public ObservableCollection<AccountItem> ExtraCalendars { get; } = [];

    public bool HasExtraCalendars => ExtraCalendars.Count > 0;

    public string DataDirectory => options.DataDirectory;

    public string LogDirectory => options.LogDirectory;

    public string ProtocolLogText => options.ProtocolLog
        ? T("Protokoll-Mitschnitt ist AKTIV (IMAP/SMTP/DAV, Passwörter geschwärzt) – nur zur Fehlersuche verwenden.")
        : T("Protokoll-Mitschnitt aus. Zum Aktivieren mit --protocol-log oder NERUNA_PROTOCOL_LOG=1 starten.");

    public bool HasAccounts => MainItems.Count > 0;

    private IReadOnlySet<Guid> _autoReplies = new HashSet<Guid>();

    /// <summary>The mail connections whose out-of-office reply is on: their "Abwesenheit" button says so.</summary>
    public void ShowAutoReplies(IReadOnlySet<Guid> activeConnections)
    {
        _autoReplies = activeConnections;
        foreach (var item in Items)
        {
            item.HasAutoReply = HasAutoReply(item.Account);
        }
    }

    private IReadOnlyDictionary<Guid, string?> _signInNeeded = new Dictionary<Guid, string?>();
    private readonly HashSet<Guid> _signingIn = [];

    // Signed in again, but the sync after it still failed: the server's error is shown with the button.
    private readonly HashSet<Guid> _signInFailed = [];

    /// <summary>
    /// The accounts whose sign-in expired or whose password was refused (with the server's error): shown in red with
    /// "Wieder anmelden". One being signed in again keeps showing that until <see cref="SignedIn"/>.
    /// </summary>
    public void ShowSignInProblems(IReadOnlyDictionary<Guid, string?> accounts)
    {
        _signInNeeded = accounts;
        foreach (var item in Items)
        {
            Apply(item);
        }
    }

    /// <summary>Signed in again (browser or password): "Wird angemeldet …" until the next sync of it has run.</summary>
    public void SigningIn(Guid accountId)
    {
        _signingIn.Add(accountId);
        foreach (var item in Items.Where(i => i.Account.Id == accountId))
        {
            Apply(item);
        }
    }

    /// <summary>The sync after signing in again ran: the warning goes, or comes back with the error.</summary>
    public void SignedIn(Guid accountId)
    {
        _signingIn.Remove(accountId);
        if (_signInNeeded.ContainsKey(accountId))
        {
            _signInFailed.Add(accountId);
        }

        foreach (var item in Items.Where(i => i.Account.Id == accountId))
        {
            Apply(item);
        }
    }

    private void Apply(AccountItem item)
    {
        if (!_signInNeeded.ContainsKey(item.Account.Id))
        {
            _signInFailed.Remove(item.Account.Id);
        }

        item.IsSigningIn = _signingIn.Contains(item.Account.Id);
        item.SignInError = _signInFailed.Contains(item.Account.Id) ? _signInNeeded.GetValueOrDefault(item.Account.Id) : null;
        item.NeedsSignIn = _signInNeeded.ContainsKey(item.Account.Id);
    }

    private bool HasAutoReply(Account account) => account.ConnectionsOf(ServiceKind.Mail).Any(c => _autoReplies.Contains(c.Id));

    public async Task ReloadAsync()
    {
        Items.Clear();
        var organisations = await cloud.GetConnectionsAsync();
        foreach (var account in await accounts.GetAccountsAsync())
        {
            var item = new AccountItem(account, setup.Summary) { HasAutoReply = HasAutoReply(account) };
            Apply(item);
            if (account.CloudId is { } cloudId && organisations.Count > 1 && organisations.FirstOrDefault(c => c.Owns(cloudId)) is { } organisation)
            {
                item.ManagedBy = F("Von {0} verwaltet", organisation.OrganizationName);
            }

            item.SetOffline(await mail.GetOfflinePeriodAsync(account.Id), period => _ = mail.SetOfflinePeriodAsync(account.Id, period));
            await ShowColorAsync(item);
            Items.Add(item);
        }

        UpdatePositions();

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasExtraCalendars));
        var transferable = (await transfer.GetTransferableAsync()).Select(a => a.Id).ToHashSet();
        foreach (var item in Items)
        {
            item.CanTransfer = transferable.Contains(item.Account.Id);
        }
    }

    [RelayCommand]
    private void AddAccount() => AddAccountRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Subscribe() => SubscribeRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>"Hinzufügen → Geburtstage, Feiertage, Internet-Kalender …": to Kalender → Zusatzkalender.</summary>
    public event EventHandler? ExtrasRequested;

    [RelayCommand]
    private void ShowExtras() => ExtrasRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddSpecial() => SpecialRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddPst() => PstRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Edit(AccountItem item) => EditRequested?.Invoke(this, item.Account);

    [RelayCommand]
    private void AutoReply(AccountItem item) => AutoReplyRequested?.Invoke(this, item.Account);

    [RelayCommand]
    private async Task RemoveAsync(AccountItem item)
    {
        if (!item.ConfirmRemove)
        {
            item.ConfirmRemove = true;
            return;
        }

        await accounts.DeleteAccountAsync(item.Account.Id);
        foreach (var connection in item.Account.Connections)
        {
            await credentials.DeleteSecretAsync(connection.Id);
            if (connection.ProviderId == ProviderIds.Pst)
            {
                Neruna.Providers.Pst.PstMailProviderFactory.Close(connection); // the file is free again (to move or delete it)
            }
        }

        await ReloadAsync();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task MoveUpAsync(AccountItem item) => MoveAsync(item, -1);

    [RelayCommand]
    private Task MoveDownAsync(AccountItem item) => MoveAsync(item, +1);

    // Within its own list (accounts, or the extra calendars); the accounts stay before the extra calendars.
    private async Task MoveAsync(AccountItem item, int delta)
    {
        var list = item.IsExtraCalendar ? ExtraCalendars : MainItems;
        var index = list.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return;
        }

        list.Move(index, target);
        Items.Clear();
        foreach (var each in MainItems.Concat(ExtraCalendars))
        {
            Items.Add(each);
        }

        SetPositions(MainItems);
        SetPositions(ExtraCalendars);
        await accounts.SetOrderAsync(Items.Select(i => i.Account.Id).ToList());
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    // The two lists from Items (in the saved order), each with its first and last for "Nach oben / Nach unten".
    private void UpdatePositions()
    {
        MainItems.Clear();
        ExtraCalendars.Clear();
        foreach (var item in Items)
        {
            (item.IsExtraCalendar ? ExtraCalendars : MainItems).Add(item);
        }

        SetPositions(MainItems);
        SetPositions(ExtraCalendars);
    }

    private static void SetPositions(ObservableCollection<AccountItem> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            list[i].IsFirst = i == 0;
            list[i].IsLast = i == list.Count - 1;
        }
    }

    [RelayCommand]
    private Task OpenLogFolderAsync()
    {
        Directory.CreateDirectory(options.LogDirectory);
        return files.OpenFolderAsync(options.LogDirectory);
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => files.OpenFolderAsync(options.DataDirectory);
}

internal sealed partial class AccountItem(Account account, Func<ServiceConnection, string> summary) : ObservableObject
{
    /// <summary>The out-of-office reply is on: the button is highlighted and says so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoReplyText))]
    public partial bool HasAutoReply { get; set; }

    public string AutoReplyText => HasAutoReply ? T("Abwesend · aktiv") : T("Abwesenheit");

    /// <summary>The last sync failed because the sign-in expired or the password was refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    public partial bool NeedsSignIn { get; set; }

    /// <summary>Signed in again, the sync that shows whether it worked is still running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn), nameof(CanSignIn))]
    public partial bool IsSigningIn { get; set; }

    /// <summary>What the server said after signing in again did not help (under the hint).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInError))]
    public partial string? SignInError { get; set; }

    /// <summary>The server's error only when it says more than the hint above it (not "Die Anmeldung ist abgelaufen" twice).</summary>
    public bool HasSignInError => !string.IsNullOrEmpty(SignInError) && SignInError != T("Die Anmeldung ist abgelaufen – bitte erneut anmelden.");

    /// <summary>The red box: the sign-in is needed, or one is being checked.</summary>
    public bool ShowSignIn => NeedsSignIn || IsSigningIn;

    public bool CanSignIn => !IsSigningIn;

    /// <summary>What the button does: sign in with the provider, or enter the password anew.</summary>
    public string SignInText => UsesProviderSignIn ? T("Wieder anmelden") : T("Passwort eingeben");

    public string SignInHint => UsesProviderSignIn
        ? T("Die Anmeldung ist abgelaufen – bitte wieder anmelden, sonst wird dieses Konto nicht mehr synchronisiert.")
        : T("Der Server lehnt die Anmeldung ab – wurde das Passwort geändert? Bitte neu eingeben, sonst wird dieses Konto nicht mehr synchronisiert.");

    private bool UsesProviderSignIn => Account.Connections.Any(c => c.ProviderId is ProviderIds.Google or ProviderIds.Graph);

    public Account Account { get; } = account;

    public string Title => Account.Title;

    /// <summary>Webmail, instructions and support of the provider (from its configuration).</summary>
    public IReadOnlyList<LinkItem> Links { get; } = ProviderLinks.Of(account);

    public bool HasLinks => Links.Count > 0;

    /// <summary>Set up by the organisation in Neruna Cloud: it goes when the organisation removes it, not here.</summary>
    public bool IsFromCloud => Account.IsFromCloud;

    /// <summary>"Von Ihrer Organisation verwaltet", or with several organisations the one that set it up.</summary>
    public string ManagedBy { get; set; } = T("Von Ihrer Organisation verwaltet");

    /// <summary>Under the title: sender name and address (the title may be a label like "Privat").</summary>
    public string Sender => Account.EmailAddress is { } email ? $"{Account.DisplayName} <{email}>"
        : Account.Connections.FirstOrDefault(c => c.ProviderId == ProviderIds.Pst)?.Settings.GetValueOrDefault(Neruna.Providers.Pst.PstMailProvider.PathSetting)
          ?? Account.DisplayName;

    public string? Email => Account.EmailAddress;

    public bool HasMail => Account.ConnectionsOf(ServiceKind.Mail).Any();

    /// <summary>An Outlook data file: read only – no out-of-office, no offline choice, nothing to transfer.</summary>
    public bool IsDataFile => Account.Connections.Any(c => c.ProviderId == ProviderIds.Pst);

    /// <summary>"Konto entfernen …" or, for an extra calendar, "Kalender entfernen …" (under "⋯").</summary>
    public string RemoveText => IsExtraCalendar ? T("Kalender entfernen …") : T("Konto entfernen …");

    public string RemoveQuestion => IsExtraCalendar
        ? T("Diesen Kalender entfernen? Er lässt sich jederzeit wieder hinzufügen.")
        : T("Dieses Konto samt seinen Daten auf diesem Computer entfernen? Auf dem Server bleibt alles.");

    /// <summary>Birthdays, holidays or a subscribed internet calendar: shown under Kalender → Zusatzkalender.</summary>
    public bool IsExtraCalendar => Account.Connections.Count > 0
        && Account.Connections.All(c => c.ProviderId is ProviderIds.Birthdays or ProviderIds.Holidays or ProviderIds.Ics);

    /// <summary>Mail of a server (out-of-office and offline choice apply).</summary>
    public bool HasServerMail => HasMail && !IsDataFile;

    /// <summary>"⋯ → Offline verfügbar": how far back the mail is complete on this computer (content and attachments).</summary>
    public IReadOnlyList<OfflineChoice> OfflineChoices { get; private set; } = [];

    /// <summary>On the card: "Offline: 3 Monate" – the choice, without opening the menu.</summary>
    [ObservableProperty]
    public partial string? OfflineText { get; set; }

    /// <summary>"⋯ → Farbe in der Ordnerliste": none or one of <see cref="AccountColors.Choices"/>.</summary>
    public IReadOnlyList<AccountColorChoice> ColorChoices { get; private set; } = [];

    /// <summary>The chosen color as a strip on the card (transparent without one).</summary>
    [ObservableProperty]
    public partial IBrush ColorStrip { get; set; } = Brushes.Transparent;

    internal void SetColor(string? color, Func<string?, Task> save)
    {
        var choices = new[] { (string?)null }.Concat(AccountColors.Choices).Select(c => new AccountColorChoice(c) { IsChecked = c == color }).ToList();
        foreach (var choice in choices)
        {
            choice.Chosen = () =>
            {
                foreach (var other in choices)
                {
                    other.IsChecked = other == choice;
                }

                ColorStrip = choice.Swatch;
                HasColor = choice.HasColor;
                _ = save(choice.Color);
            };
        }

        ColorStrip = choices.First(c => c.IsChecked is true || c.Color == color).Swatch;
        HasColor = color is not null;
        ColorChoices = choices;
        OnPropertyChanged(nameof(ColorChoices));
    }

    /// <summary>A color is chosen (otherwise the card shows an empty swatch to click).</summary>
    [ObservableProperty]
    public partial bool HasColor { get; set; }

    /// <summary>The offline period shown – and chosen – right on the card.</summary>
    [ObservableProperty]
    public partial OfflineChoice? SelectedOffline { get; set; }

    private bool _settingOffline;

    partial void OnSelectedOfflineChanged(OfflineChoice? value)
    {
        if (!_settingOffline && value is not null)
        {
            value.Chosen?.Invoke();
        }
    }

    internal void SetOffline(Neruna.Core.Mail.OfflinePeriod period, Action<Neruna.Core.Mail.OfflinePeriod> save)
    {
        var choices = Neruna.Core.Mail.OfflinePeriods.All.Select(p => new OfflineChoice(p) { IsChecked = p == period }).ToList();
        foreach (var choice in choices)
        {
            choice.Chosen = () =>
            {
                foreach (var other in choices)
                {
                    other.IsChecked = other == choice;
                }

                OfflineText = F("Offline: {0}", choice.Label);
                save(choice.Period);
            };
        }

        OfflineText = F("Offline: {0}", Neruna.Core.Mail.OfflinePeriods.Label(period));
        OfflineChoices = choices;
        OnPropertyChanged(nameof(OfflineChoices));
        _settingOffline = true;
        SelectedOffline = choices.FirstOrDefault(c => c.IsChecked);
        _settingOffline = false;
    }

    /// <summary>The app can take the account over (own account with IMAP, CalDAV or CardDAV).</summary>
    [ObservableProperty]
    public partial bool CanTransfer { get; set; }

    /// <summary>Under the title: the services, e.g. "E-Mail · Kalender · Kontakte".</summary>
    public string Services => IsDataFile ? T("Outlook-Datendatei · nur lesen") : string.Join(" · ", Account.Connections.Select(c => c.Kind).Distinct().Order().Select(k => k switch
    {
        ServiceKind.Mail => T("E-Mail"),
        ServiceKind.Calendar => T("Kalender"),
        _ => T("Kontakte"),
    }));

    public IReadOnlyList<ConnectionLine> Connections { get; } = account.Connections.OrderBy(c => c.Kind).Select(c => ConnectionLine.From(c, summary(c))).ToList();

    [ObservableProperty]
    public partial bool ConfirmRemove { get; set; }

    [RelayCommand]
    private void CancelRemove() => ConfirmRemove = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveUp))]
    public partial bool IsFirst { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveDown))]
    public partial bool IsLast { get; set; }

    public bool CanMoveUp => !IsFirst;

    public bool CanMoveDown => !IsLast;
}

internal sealed record ConnectionLine(string Kind, string Provider, string Detail)
{
    /// <param name="detail">The provider's one-line description of the server.</param>
    public static ConnectionLine From(ServiceConnection connection, string detail)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var kind = connection.Kind switch
        {
            ServiceKind.Mail => T("E-Mail"),
            ServiceKind.Calendar => T("Kalender"),
            _ => T("Kontakte"),
        };

        return new ConnectionLine(kind, connection.ProviderId.ToUpperInvariant(), detail);
    }
}

/// <summary>A choice in "⋯ → Offline verfügbar" (a radio item).</summary>
internal sealed partial class OfflineChoice(Neruna.Core.Mail.OfflinePeriod period) : ObservableObject
{
    public Neruna.Core.Mail.OfflinePeriod Period { get; } = period;

    public string Label { get; } = Neruna.Core.Mail.OfflinePeriods.Label(period);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    internal Action? Chosen { get; set; }

    [RelayCommand]
    private void Choose() => Chosen?.Invoke();
}
