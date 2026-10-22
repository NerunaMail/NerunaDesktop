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

    /// <summary>An account got another color: the folder list takes it.</summary>
    public event EventHandler? ColorsChanged;

    /// <summary>Raised after an account was removed, so the other pages reload.</summary>
    public event EventHandler? AccountsChanged;

    public ObservableCollection<AccountItem> Items { get; } = [];

    public string DataDirectory => options.DataDirectory;

    public string LogDirectory => options.LogDirectory;

    public string ProtocolLogText => options.ProtocolLog
        ? T("Protokoll-Mitschnitt ist AKTIV (IMAP/SMTP/DAV, Passwörter geschwärzt) – nur zur Fehlersuche verwenden.")
        : T("Protokoll-Mitschnitt aus. Zum Aktivieren mit --protocol-log oder NERUNA_PROTOCOL_LOG=1 starten.");

    public bool HasAccounts => Items.Count > 0;

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

    private bool HasAutoReply(Account account) => account.ConnectionsOf(ServiceKind.Mail).Any(c => _autoReplies.Contains(c.Id));

    public async Task ReloadAsync()
    {
        Items.Clear();
        var organisations = await cloud.GetConnectionsAsync();
        foreach (var account in await accounts.GetAccountsAsync())
        {
            var item = new AccountItem(account, setup.Summary) { HasAutoReply = HasAutoReply(account) };
            if (account.CloudId is { } cloudId && organisations.Count > 1 && organisations.FirstOrDefault(c => c.Owns(cloudId)) is { } organisation)
            {
                item.ManagedBy = F("Von {0} verwaltet", organisation.OrganizationName);
            }

            item.SetOffline(await mail.GetOfflinePeriodAsync(account.Id), period => _ = mail.SetOfflinePeriodAsync(account.Id, period));
            var accountId = account.Id;
            item.SetColor(await AccountColors.GetAsync(settings, accountId), async color =>
            {
                await AccountColors.SetAsync(settings, accountId, color);
                ColorsChanged?.Invoke(this, EventArgs.Empty);
            });
            Items.Add(item);
        }

        UpdatePositions();

        OnPropertyChanged(nameof(HasAccounts));
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

    private async Task MoveAsync(AccountItem item, int delta)
    {
        var index = Items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        UpdatePositions();
        await accounts.SetOrderAsync(Items.Select(i => i.Account.Id).ToList());
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePositions()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsFirst = i == 0;
            Items[i].IsLast = i == Items.Count - 1;
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
                _ = save(choice.Color);
            };
        }

        ColorStrip = choices.First(c => c.IsChecked is true || c.Color == color).Swatch;
        ColorChoices = choices;
        OnPropertyChanged(nameof(ColorChoices));
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
    [NotifyPropertyChangedFor(nameof(RemoveText))]
    public partial bool ConfirmRemove { get; set; }

    public string RemoveText => ConfirmRemove ? T("Wirklich entfernen?") : T("Entfernen");

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
