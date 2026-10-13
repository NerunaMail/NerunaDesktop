using System.Security.Cryptography;
using System.Text.Json;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Vault;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>A QR code for the app and the PIN shown next to it.</summary>
public sealed record AccountTransferCode(string Code, string Pin);

/// <summary>The transfer code cannot be used; <see cref="Exception.Message"/> is for the user.</summary>
public sealed class AccountTransferException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Own accounts from the desktop to the phone by QR code and PIN (<see cref="AccountTransfer"/>), for Neruna used
/// without Neruna Cloud – with it, accounts come along with the backup. Server settings and password, nothing goes over
/// a server. Only mail accounts the app can use (IMAP).
/// </summary>
public sealed class AccountTransferService(IAccountStore accounts, ICredentialStore credentials)
{
    /// <summary>Own accounts with an IMAP mailbox.</summary>
    public async Task<IReadOnlyList<Account>> GetTransferableAsync(CancellationToken cancellationToken = default) =>
        (await accounts.GetAccountsAsync(cancellationToken)).Where(a => !a.IsFromCloud && a.Connections.Any(IsTransferable)).ToList();

    /// <summary>The QR code for <paramref name="account"/> and a new PIN for it.</summary>
    public async Task<AccountTransferCode> CreateCodeAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var connections = new List<BackupConnection>();
        foreach (var connection in account.Connections.Where(IsTransferable))
        {
            connections.Add(new BackupConnection(connection.Id, connection.Kind, connection.ProviderId, connection.Settings.ToDictionary(),
                await credentials.GetSecretAsync(connection.Id, cancellationToken)));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new BackupAccount(account.Id, account.DisplayName, account.EmailAddress, account.Label, connections, account.Aliases));
        var pin = AccountTransfer.NewPin();
        return new AccountTransferCode(AccountTransfer.Seal(payload, pin), pin);
    }

    /// <summary>A Neruna account code (and not, say, a pairing code).</summary>
    public static bool IsCode(string? text) => AccountTransfer.IsTransfer(text);

    /// <summary>On the phone: sets up the account from a scanned code (same id as on the desktop: scanning again updates it).</summary>
    public async Task<Account> ImportAsync(string code, string pin, CancellationToken cancellationToken = default)
    {
        if (!IsCode(code))
        {
            throw new AccountTransferException(T("Das ist kein Neruna-Konto-Code. Bitte am Computer unter Einstellungen → Konten «Konto aufs Handy übertragen» wählen."));
        }

        BackupAccount? transferred;
        try
        {
            var data = await Task.Run(() => AccountTransfer.Open(code, pin), cancellationToken);
            transferred = JsonSerializer.Deserialize<BackupAccount>(data);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            throw new AccountTransferException(T("Die PIN stimmt nicht."), ex);
        }

        if (transferred is null || transferred.Connections.Count == 0)
        {
            throw new AccountTransferException(T("Dieser QR-Code enthält kein Konto."));
        }

        var existing = (await accounts.GetAccountsAsync(cancellationToken))
            .FirstOrDefault(a => a.Id != transferred.Id && !a.IsFromCloud && a.EmailAddress is not null
                                 && string.Equals(a.EmailAddress, transferred.EmailAddress, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            throw new AccountTransferException(F("{0} ist auf diesem Handy schon eingerichtet.", existing.Title));
        }

        var account = new Account(transferred.Id, transferred.DisplayName, transferred.EmailAddress,
            transferred.Connections.Select(c => new ServiceConnection(c.Id, c.Kind, c.ProviderId, c.Settings)).ToList(), transferred.Label, transferred.Aliases);
        await accounts.SaveAccountAsync(account, cancellationToken);
        foreach (var transferredConnection in transferred.Connections)
        {
            if (transferredConnection.Secret is { } secret)
            {
                await credentials.SetSecretAsync(transferredConnection.Id, secret, cancellationToken);
            }
        }

        return account;
    }

    private static bool IsTransferable(ServiceConnection connection) => connection.Kind == ServiceKind.Mail && connection.ProviderId == ProviderIds.Imap;
}
