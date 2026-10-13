using System.Collections.ObjectModel;
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
    Neruna.Core.Cloud.AccountTransferService transfer) : ViewModelBase
{
    // ── Konto aufs Handy übertragen: QR code + PIN for the Neruna app, after the computer login ──

    public Infrastructure.LoginGate Login { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransferring))]
    public partial IReadOnlyList<Neruna.Core.Accounts.Account>? TransferAccounts { get; set; }

    public bool IsTransferring => TransferAccounts is not null;

    [ObservableProperty]
    public partial Neruna.Core.Accounts.Account? TransferAccount { get; set; }

    [ObservableProperty]
    public partial Avalonia.Media.Imaging.Bitmap? TransferQrCode { get; set; }

    [ObservableProperty]
    public partial string? TransferPin { get; set; }

    [RelayCommand]
    private async Task StartTransferAsync()
    {
        var list = await transfer.GetTransferableAsync();
        if (list.Count == 0 || !await Login.VerifyAsync(T("Zum Übertragen eines Kontos aufs Handy bitte mit dem Login dieses Computers bestätigen.")))
        {
            return;
        }

        TransferAccount = null;
        TransferAccounts = list;
        TransferAccount = list[0];
    }

    partial void OnTransferAccountChanged(Neruna.Core.Accounts.Account? value) => _ = ShowTransferCodeAsync(value);

    private async Task ShowTransferCodeAsync(Neruna.Core.Accounts.Account? account)
    {
        TransferQrCode = null;
        TransferPin = null;
        if (account is null)
        {
            return;
        }

        var code = await transfer.CreateCodeAsync(account);
        // Error correction L (a screen does not get dirty) keeps the code coarse; whole pixels per module, about 500 pixels
        // wide and shown unscaled, keep it sharp enough for a phone camera.
        var data = new QRCoder.QRCodeGenerator().CreateQrCode(code.Code, QRCoder.QRCodeGenerator.ECCLevel.L);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(Math.Max(4, 500 / data.ModuleMatrix.Count));
        using var stream = new MemoryStream(png);
        TransferQrCode = new Avalonia.Media.Imaging.Bitmap(stream);
        TransferPin = code.Pin;
    }

    [RelayCommand]
    private void CloseTransfer()
    {
        TransferAccounts = null;
        TransferAccount = null;
        TransferQrCode = null;
        TransferPin = null;
    }

    public event EventHandler? AddAccountRequested;

    public event EventHandler? SubscribeRequested;

    /// <summary>"Bearbeiten": the shell shows the setup dialog with this account.</summary>
    public event EventHandler<Neruna.Core.Accounts.Account>? EditRequested;

    /// <summary>"Abwesenheit" on an account with mail.</summary>
    public event EventHandler<Neruna.Core.Accounts.Account>? AutoReplyRequested;

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
        foreach (var account in await accounts.GetAccountsAsync())
        {
            Items.Add(new AccountItem(account, setup.Summary) { HasAutoReply = HasAutoReply(account) });
        }

        UpdatePositions();

        OnPropertyChanged(nameof(HasAccounts));
        CanTransfer = (await transfer.GetTransferableAsync()).Count > 0;
    }

    /// <summary>There are own IMAP accounts the app can take over.</summary>
    [ObservableProperty]
    public partial bool CanTransfer { get; set; }

    [RelayCommand]
    private void AddAccount() => AddAccountRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Subscribe() => SubscribeRequested?.Invoke(this, EventArgs.Empty);

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

    /// <summary>Set up by the organisation in Neruna Cloud: it goes when the organisation removes it, not here.</summary>
    public bool IsFromCloud => Account.IsFromCloud;

    /// <summary>Under the title: sender name and address (the title may be a label like "Privat").</summary>
    public string Sender => Account.EmailAddress is { } email ? $"{Account.DisplayName} <{email}>" : Account.DisplayName;

    public string? Email => Account.EmailAddress;

    public bool HasEmail => Email is not null;

    public bool HasMail => Account.ConnectionsOf(ServiceKind.Mail).Any();

    public IReadOnlyList<ConnectionLine> Connections { get; } = account.Connections.OrderBy(c => c.Kind).Select(c => ConnectionLine.From(c, summary(c))).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveText))]
    public partial bool ConfirmRemove { get; set; }

    public string RemoveText => ConfirmRemove ? T("Wirklich entfernen?") : T("Entfernen");

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
