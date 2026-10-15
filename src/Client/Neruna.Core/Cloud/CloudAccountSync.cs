using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Security;
using Neruna.Vault;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>An account of the organisation that came without a password: the user enters it to finish the setup.</summary>
/// <param name="CloudId">The local id (<see cref="CloudConnection.LocalId"/>), as in <c>Account.CloudId</c>.</param>
public sealed record PendingCloudAccount(string CloudId, string DisplayName, string Email, Account Account, string ConnectionId = CloudConnection.FirstId);

/// <summary>Where the organisation's accounts stand on this device (Einstellungen → Cloud).</summary>
/// <param name="Elsewhere">Not set up: another organisation already brought an account with that address.</param>
public sealed record CloudAccountStatus(int Received, int Waiting, IReadOnlyList<PendingCloudAccount> Pending, string? Problem, int Elsewhere = 0)
{
    public static CloudAccountStatus None { get; } = new(0, 0, [], null);
}

/// <summary>
/// Sets up the mail accounts the organisation assigned to this person in the portal, zero knowledge like the
/// certificates: this device's key opens the envelope, and an account is taken only when the organisation key signed
/// it – servers included, so nobody else can point the password at another server. Server settings and aliases are the
/// organisation's (read-only here); the label is the user's. Without a password the user is asked once. Accounts no
/// longer assigned are removed. Each connected organisation on its own (keys, signing key, state).
/// </summary>
public sealed class CloudAccountSync(
    CloudController cloud,
    IAccountStore accounts,
    AccountSetupService setup,
    ICredentialStore credentials,
    ISettingsStore settings,
    ILogger<CloudAccountSync> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CloudAccountStatus> _status = new();

    /// <summary>All organisations together (the shell asks for the passwords of <see cref="CloudAccountStatus.Pending"/>).</summary>
    public CloudAccountStatus Status
    {
        get
        {
            var all = _status.Values.ToList();
            return all.Count == 0 ? CloudAccountStatus.None : new CloudAccountStatus(
                all.Sum(s => s.Received), all.Sum(s => s.Waiting), [.. all.SelectMany(s => s.Pending)], all.Select(s => s.Problem).FirstOrDefault(p => p is not null), all.Sum(s => s.Elsewhere));
        }
    }

    /// <summary>Where the accounts of one organisation stand.</summary>
    public CloudAccountStatus StatusOf(string connectionId) => _status.GetValueOrDefault(connectionId, CloudAccountStatus.None);

    public event EventHandler? Changed;

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        foreach (var gone in _status.Keys.Where(id => connections.All(c => c.Id != id)))
        {
            _status.TryRemove(gone, out _);
        }

        var changed = await RemoveDisconnectedAsync(cancellationToken);
        return await CloudSync.EachAsync(connections, c => SyncAsync(c, cancellationToken), logger, cancellationToken) | changed;
    }

    private async Task<bool> SyncAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        using var deviceKey = await CloudCertificateSync.DeviceKeyAsync(credentials, connection, logger, cancellationToken);
        var response = await cloud.GetMailAccountsAsync(connection, cancellationToken);
        var signingKey = response.OrganizationKey?.SigningPublicKey;
        var pinnedKey = connection.SettingKey(SettingKeys.CloudOrganizationSigningKey);
        var pinned = await settings.GetAsync(pinnedKey, cancellationToken);
        if (signingKey is not null && pinned is null)
        {
            await settings.SetAsync(pinnedKey, signingKey, cancellationToken);
            pinned = signingKey;
        }

        if (signingKey is not null && signingKey != pinned)
        {
            logger.LogWarning("The organisation key changed; no accounts taken");
            _status[connection.Id] = new CloudAccountStatus(0, response.Accounts.Count, [],
                T("Der Organisationsschlüssel hat sich geändert – aus Sicherheitsgründen werden keine Konten übernommen. Bitte die Cloud-Verbindung neu einrichten."));
            return false;
        }

        var state = await LoadStateAsync(connection, cancellationToken);
        var all = await accounts.GetAccountsAsync(cancellationToken);
        var local = all.Where(a => a.IsFromCloud && connection.Owns(a.CloudId!)).ToDictionary(a => a.CloudId!, StringComparer.Ordinal);
        // The same address from two organisations: the account that came first stays, the other is not set up.
        var otherOrganisations = all.Where(a => a.IsFromCloud && !connection.Owns(a.CloudId!)).Select(a => a.EmailAddress)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var elsewhere = 0;
        var offeredIds = response.Accounts.Select(a => connection.LocalId(a.Id)).ToHashSet(StringComparer.Ordinal);
        var pending = new List<PendingCloudAccount>();
        var changed = false;
        var received = 0;
        var waiting = 0;
        foreach (var offered in response.Accounts)
        {
            // With what this installation can set up: an update that brings calendars (the Android app) builds the
            // accounts again, so their CalDAV/CardDAV servers are taken too.
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(offered.Payload + "\n" + setup.Capabilities)));
            var localId = connection.LocalId(offered.Id);
            if (local.ContainsKey(localId) && state.GetValueOrDefault(localId) == fingerprint)
            {
                received++;
                continue;
            }

            if (offered.Envelope is null || signingKey is null)
            {
                waiting++;
                continue;
            }

            if (!AccountEnvelopes.Verify(offered.Id, offered.Payload, offered.PayloadSignature, signingKey))
            {
                logger.LogWarning("Account {Id} not signed by the organisation; skipped", offered.Id);
                waiting++;
                continue;
            }

            CloudAccountData data;
            try
            {
                var key = AccountEnvelopes.Unwrap(offered.Envelope, deviceKey, offered.Id, connection.DeviceId);
                data = JsonSerializer.Deserialize<CloudAccountData>(AccountEnvelopes.Decrypt(offered.Id, offered.Payload, key), NerunaJson.Options)
                       ?? throw new JsonException("empty");
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
            {
                logger.LogWarning(ex, "Account {Id} could not be opened", offered.Id);
                waiting++;
                continue;
            }

            if (otherOrganisations.Contains(data.Email))
            {
                logger.LogInformation("Account {Email} already comes from another organisation; not set up again", data.Email);
                offeredIds.Remove(localId);
                elsewhere++;
                continue;
            }

            var account = Build(localId, data, local.GetValueOrDefault(localId));
            var password = data.Password ?? await credentials.GetSecretAsync(CloudAccountPasswords.SecretId(localId), cancellationToken);
            if (password is null)
            {
                // Finished once the user typed the password (see CompleteAsync).
                pending.Add(new PendingCloudAccount(localId, data.DisplayName, data.Email, account, connection.Id));
                continue;
            }

            await SaveAsync(account, local.GetValueOrDefault(localId), password, cancellationToken);
            state[localId] = fingerprint;
            received++;
            changed = true;
            logger.LogInformation("Account {Email} set up from the organisation", data.Email);
        }

        // No longer assigned (or deleted in the portal): gone here, too.
        foreach (var gone in local.Values.Where(a => !offeredIds.Contains(a.CloudId!)))
        {
            await RemoveAsync(gone, cancellationToken);
            state.Remove(gone.CloudId!);
            changed = true;
        }

        await settings.SetAsync(connection.SettingKey(SettingKeys.CloudAccountsState), JsonSerializer.Serialize(state), cancellationToken);
        _status[connection.Id] = new CloudAccountStatus(received, waiting, pending, null, elsewhere);
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>The password for an account that came without one: tested, then the account is set up.</summary>
    /// <exception cref="AccountSetupException">The servers refused it.</exception>
    public async Task CompleteAsync(PendingCloudAccount pending, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        await setup.CreateAsync(pending.Account, password, cancellationToken);
        await CloudAccountPasswords.SetAsync(credentials, accounts, pending.CloudId, password, cancellationToken);
        if (await cloud.GetConnectionAsync(pending.ConnectionId, cancellationToken) is { } connection)
        {
            var state = await LoadStateAsync(connection, cancellationToken);
            state.Remove(pending.CloudId); // the next sync records it as set up
            await settings.SetAsync(connection.SettingKey(SettingKeys.CloudAccountsState), JsonSerializer.Serialize(state), cancellationToken);
            if (_status.TryGetValue(connection.Id, out var status))
            {
                _status[connection.Id] = status with { Pending = [.. status.Pending.Where(p => p.CloudId != pending.CloudId)] };
            }
        }

        await SyncAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Disconnected: the accounts of organisations no longer connected leave this device (with their passwords).</summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        var cloudAccounts = (await accounts.GetAccountsAsync(cancellationToken)).Where(a => a.IsFromCloud && !connections.Any(c => c.Owns(a.CloudId!))).ToList();
        foreach (var account in cloudAccounts)
        {
            await RemoveAsync(account, cancellationToken);
        }

        if (cloudAccounts.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return cloudAccounts.Count > 0;
    }

    // The account as the organisation describes it; an existing one keeps its id, connection ids (offline data) and label.
    private Account Build(string cloudId, CloudAccountData data, Account? existing)
    {
        var dav = new List<DavServerSettings>();
        if (Uri.TryCreate(data.CaldavUrl, UriKind.Absolute, out var caldav))
        {
            dav.Add(new DavServerSettings(ServerProtocol.CalDav, caldav, data.Username));
        }

        if (Uri.TryCreate(data.CarddavUrl, UriKind.Absolute, out var carddav))
        {
            dav.Add(new DavServerSettings(ServerProtocol.CardDav, carddav, data.Username));
        }

        var config = new MailProviderConfig(
            data.Email.Split('@').Last(), null,
            [new MailServerSettings(ServerProtocol.Imap, data.Imap.Host, data.Imap.Port, Security(data.Imap.Security), AuthScheme.PasswordCleartext, data.Username)],
            [new MailServerSettings(ServerProtocol.Smtp, data.Smtp.Host, data.Smtp.Port, Security(data.Smtp.Security), AuthScheme.PasswordCleartext, data.Username)],
            dav);
        var built = setup.BuildAccount(data.DisplayName, data.Email, config);
        var connections = built.Connections
            .Select(c => existing?.ConnectionsOf(c.Kind).FirstOrDefault(e => e.ProviderId == c.ProviderId) is { } kept ? c with { Id = kept.Id } : c)
            .ToList();
        return built with
        {
            Id = existing?.Id ?? built.Id,
            Connections = connections,
            Label = existing?.Label,
            Aliases = data.Aliases is { Count: > 0 } aliases ? aliases : null,
            CloudId = cloudId,
        };
    }

    private async Task SaveAsync(Account account, Account? previous, string password, CancellationToken cancellationToken)
    {
        foreach (var removed in previous?.Connections.Where(c => account.Connections.All(n => n.Id != c.Id)) ?? [])
        {
            await credentials.DeleteSecretAsync(removed.Id, cancellationToken);
        }

        await accounts.SaveAccountAsync(account, cancellationToken);
        foreach (var connection in account.Connections)
        {
            await credentials.SetSecretAsync(connection.Id, password, cancellationToken);
        }
    }

    private async Task RemoveAsync(Account account, CancellationToken cancellationToken)
    {
        await accounts.DeleteAccountAsync(account.Id, cancellationToken);
        foreach (var connection in account.Connections)
        {
            await credentials.DeleteSecretAsync(connection.Id, cancellationToken);
        }

        await credentials.DeleteSecretAsync(CloudAccountPasswords.SecretId(account.CloudId!), cancellationToken);
        logger.LogInformation("Account {Email} of the organisation removed", account.EmailAddress);
    }

    private async Task<Dictionary<string, string>> LoadStateAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(await settings.GetAsync(connection.SettingKey(SettingKeys.CloudAccountsState), cancellationToken) ?? "{}") ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SocketSecurity Security(string? value) =>
        Enum.TryParse<SocketSecurity>(value, ignoreCase: true, out var security) ? security : SocketSecurity.SslOnConnect;
}

/// <summary>The account as the portal encrypted it (public/js/neruna-certificates.js, encryptAccount).</summary>
internal sealed record CloudAccountData(
    string DisplayName,
    string Email,
    string Username,
    string? Password,
    IReadOnlyList<MailIdentity>? Aliases,
    CloudServer Imap,
    CloudServer Smtp,
    string? CaldavUrl,
    string? CarddavUrl);

internal sealed record CloudServer(string Host, int Port, string? Security);
