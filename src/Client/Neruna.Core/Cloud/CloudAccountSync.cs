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

/// <summary>
/// An account of the organisation that needs the user once: a password (IMAP, Exchange, CalDAV/CardDAV), the password
/// from Proton Mail Bridge, or a sign-in in the browser (Microsoft 365, Google, Nextcloud – see <see cref="SignsIn"/>).
/// </summary>
/// <param name="CloudId">The local id (<see cref="CloudConnection.LocalId"/>), as in <c>Account.CloudId</c>.</param>
/// <param name="Kind">One of <see cref="CloudAccountKinds"/>.</param>
/// <param name="Server">Nextcloud: its address.</param>
public sealed record PendingCloudAccount(string CloudId, string DisplayName, string Email, Account Account, string ConnectionId = CloudConnection.FirstId,
    string Kind = CloudAccountKinds.Imap, string? Server = null, CloudAccountData? Data = null)
{
    /// <summary>Set up by signing in in the browser (no password to type).</summary>
    public bool SignsIn => CloudAccountKinds.SignsIn(Kind);
}

/// <summary>The kinds of account an organisation sets up in Neruna Cloud (Portal → E-Mail-Konten).</summary>
public static class CloudAccountKinds
{
    public const string Imap = "imap";
    public const string Exchange = "exchange";
    public const string Microsoft365 = "microsoft365";
    public const string Google = "google";
    public const string Proton = "proton";
    public const string Nextcloud = "nextcloud";
    public const string Dav = "dav";

    /// <summary>The person signs in on the device; the organisation gives only who and the defaults.</summary>
    public static bool SignsIn(string kind) => kind is Microsoft365 or Google or Nextcloud;
}

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
    ILogger<CloudAccountSync> logger,
    Discovery.AccountDiscovery? discovery = null)
{
    /// <summary>
    /// Kinds this app sets up with the user's help (sign-in in the browser, Proton Mail Bridge): set by the app that
    /// can (Neruna Desktop). IMAP and – with their providers – Exchange and CalDAV/CardDAV need nothing of it.
    /// </summary>
    public IReadOnlyCollection<string> InteractiveKinds { get; set; } = [];

    /// <summary>What the server is told this app can set up (it lists only those accounts).</summary>
    public string SupportedKinds
    {
        get
        {
            var capabilities = setup.Capabilities.Split(',');
            var kinds = new List<string> { CloudAccountKinds.Imap };
            if (capabilities.Contains("mail:" + Providers.ProviderIds.Ews))
            {
                kinds.Add(CloudAccountKinds.Exchange);
            }

            if (capabilities.Contains("calendar:" + Providers.ProviderIds.CalDav) || capabilities.Contains("contacts:" + Providers.ProviderIds.CardDav))
            {
                kinds.Add(CloudAccountKinds.Dav);
            }

            kinds.AddRange(InteractiveKinds.Where(k => !kinds.Contains(k)));
            return string.Join(",", kinds);
        }
    }

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
        var response = await cloud.GetMailAccountsAsync(connection, SupportedKinds, cancellationToken);
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

            var kind = data.Kind ?? CloudAccountKinds.Imap;
            var existing = local.GetValueOrDefault(localId);
            if (CloudAccountKinds.SignsIn(kind))
            {
                if (existing is null)
                {
                    // Finished once the user signed in (see AdoptAsync).
                    pending.Add(new PendingCloudAccount(localId, data.DisplayName, data.Email, Placeholder(localId, data), connection.Id, kind, data.NextcloudUrl, data));
                    continue;
                }

                // Signed in already: only names, reply address and signatures follow the organisation.
                await ApplyAsync(Identity(existing, data), data, connection, cancellationToken);
                state[localId] = fingerprint;
                received++;
                changed = true;
                continue;
            }

            Account account;
            try
            {
                account = await BuildAsync(localId, data, existing, cancellationToken);
            }
            catch (AccountSetupException ex)
            {
                logger.LogWarning("Account {Email} of the organisation not set up: {Reason}", data.Email, ex.Message);
                waiting++;
                continue;
            }

            // Proton: the Bridge password is this computer's own – never from the organisation.
            var password = (kind == CloudAccountKinds.Proton ? null : data.Password) ?? await credentials.GetSecretAsync(CloudAccountPasswords.SecretId(localId), cancellationToken);
            if (password is null)
            {
                // Finished once the user typed the password (see CompleteAsync).
                pending.Add(new PendingCloudAccount(localId, data.DisplayName, data.Email, account, connection.Id, kind, Data: data));
                continue;
            }

            await SaveAsync(account, existing, password, cancellationToken);
            await AssignSignaturesAsync(account, data, connection, cancellationToken);
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
        await ForgetPendingAsync(pending, cancellationToken);
        await SyncAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// An account of the organisation the user just signed in to (Microsoft 365, Google, Nextcloud – set up and saved by
    /// the sign-in): it becomes the organisation's, with its names, reply address and signatures. Microsoft and Google
    /// must have signed in with the organisation's address.
    /// </summary>
    /// <exception cref="AccountSetupException">Signed in with another address (the account is removed again).</exception>
    public async Task AdoptAsync(PendingCloudAccount pending, Account signedIn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(signedIn);
        if (pending.Kind != CloudAccountKinds.Nextcloud && !string.Equals(signedIn.EmailAddress, pending.Email, StringComparison.OrdinalIgnoreCase))
        {
            await RemoveAsync(signedIn with { CloudId = pending.CloudId }, cancellationToken);
            throw new AccountSetupException(F("Angemeldet als {0} – Ihre Organisation hat das Konto für {1} eingerichtet. Bitte mit dieser Adresse anmelden.",
                signedIn.EmailAddress ?? "?", pending.Email));
        }

        var connection = await cloud.GetConnectionAsync(pending.ConnectionId, cancellationToken);
        var data = pending.Data;
        var account = signedIn with
        {
            CloudId = pending.CloudId,
            DisplayName = pending.Account.DisplayName,
            EmailAddress = signedIn.EmailAddress ?? pending.Account.EmailAddress,
            Label = pending.Kind == CloudAccountKinds.Nextcloud ? signedIn.Label : null,
        };
        if (data is not null && connection is not null)
        {
            await ApplyAsync(account, data, connection, cancellationToken);
        }
        else
        {
            await accounts.SaveAccountAsync(account, cancellationToken);
        }

        await ForgetPendingAsync(pending, cancellationToken);
        await SyncAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ForgetPendingAsync(PendingCloudAccount pending, CancellationToken cancellationToken)
    {
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

    /// <summary>EWS: how to sign in (Neruna.Providers.Ews.EwsSettings.AuthenticationKey).</summary>
    private const string EwsAuthenticationKey = "ews.auth";

    // The account as the organisation describes it; an existing one keeps its id, connection ids (offline data) and label.
    private async Task<Account> BuildAsync(string cloudId, CloudAccountData data, Account? existing, CancellationToken cancellationToken)
    {
        var kind = data.Kind ?? CloudAccountKinds.Imap;
        var username = string.IsNullOrWhiteSpace(data.Username) ? data.Email : data.Username;
        var incoming = new List<MailServerSettings>();
        var outgoing = new List<MailServerSettings>();
        var dav = new List<DavServerSettings>();
        var options = new Dictionary<string, string>();
        switch (kind)
        {
            case CloudAccountKinds.Exchange:
                var ews = Uri.TryCreate(data.Ews?.Url, UriKind.Absolute, out var given) ? given : null;
                if (ews is null && discovery is not null)
                {
                    // No address given: as when adding it by hand – the domain's configuration or Neruna's directory.
                    ews = (await discovery.DiscoverAsync(data.Email, cancellationToken))?.Config.Ews?.ServerUrl;
                }

                dav.Add(new DavServerSettings(ServerProtocol.Ews, ews ?? throw new AccountSetupException(T("Der Exchange-Server wurde nicht gefunden.")), username));
                if (data.Ews?.Authentication is { Length: > 0 } auth && auth != "Automatic")
                {
                    options[EwsAuthenticationKey] = auth;
                }

                break;
            case CloudAccountKinds.Proton:
                // Proton Mail Bridge on this computer, at its usual ports (the app finds the actual ones when asking for
                // the Bridge password); a rebuilt account keeps what was found then (see below).
                incoming.Add(new MailServerSettings(ServerProtocol.Imap, ProtonHost, 1143, SocketSecurity.StartTls, AuthScheme.PasswordCleartext, username));
                outgoing.Add(new MailServerSettings(ServerProtocol.Smtp, ProtonHost, 1025, SocketSecurity.StartTls, AuthScheme.PasswordCleartext, username));
                break;
            case CloudAccountKinds.Dav:
                AddDav(dav, data, username);
                if (dav.Count == 0)
                {
                    throw new AccountSetupException(T("Keine CalDAV- oder CardDAV-Adresse."));
                }

                break;
            default:
                if (data.Imap is null || data.Smtp is null)
                {
                    throw new AccountSetupException(T("Keine Servereinstellungen."));
                }

                incoming.Add(new MailServerSettings(ServerProtocol.Imap, data.Imap.Host, data.Imap.Port, Security(data.Imap.Security), AuthScheme.PasswordCleartext, username));
                outgoing.Add(new MailServerSettings(ServerProtocol.Smtp, data.Smtp.Host, data.Smtp.Port, Security(data.Smtp.Security), AuthScheme.PasswordCleartext, username));
                AddDav(dav, data, username);
                break;
        }

        var config = new MailProviderConfig(data.Email.Split('@').Last(), null, incoming, outgoing, dav);
        var built = setup.BuildAccount(data.DisplayName, data.Email, config, options: options);
        var connections = built.Connections
            .Select(c => existing?.ConnectionsOf(c.Kind).FirstOrDefault(e => e.ProviderId == c.ProviderId) is { } kept
                // Proton: what this computer found (ports, the Bridge's certificate) stays with the connection.
                ? c with { Id = kept.Id, Settings = kind == CloudAccountKinds.Proton ? Merge(c.Settings, kept.Settings) : c.Settings }
                : c)
            .ToList();
        return Identity(built with { Id = existing?.Id ?? built.Id, Connections = connections, Label = existing?.Label }, data) with { CloudId = cloudId };
    }

    private const string ProtonHost = "127.0.0.1";

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> kept, IReadOnlyDictionary<string, string> given)
    {
        var merged = new Dictionary<string, string>(kept);
        foreach (var (key, value) in given)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static void AddDav(List<DavServerSettings> dav, CloudAccountData data, string username)
    {
        if (Uri.TryCreate(data.CaldavUrl, UriKind.Absolute, out var caldav))
        {
            dav.Add(new DavServerSettings(ServerProtocol.CalDav, caldav, username));
        }

        if (Uri.TryCreate(data.CarddavUrl, UriKind.Absolute, out var carddav))
        {
            dav.Add(new DavServerSettings(ServerProtocol.CardDav, carddav, username));
        }
    }

    /// <summary>What the organisation decides of any account: sender name, aliases and the fixed reply address.</summary>
    private static Account Identity(Account account, CloudAccountData data)
    {
        var connections = account.Connections
            .Select(c => c.Kind != ServiceKind.Mail ? c
                : string.IsNullOrWhiteSpace(data.ReplyTo)
                    ? c with { Settings = Without(c.Settings, Mail.ReplyAddress.CloudSetting) }
                    : c with { Settings = new Dictionary<string, string>(c.Settings) { [Mail.ReplyAddress.CloudSetting] = data.ReplyTo.Trim() } }) // the organisation's: fixed here
            .ToList();
        return account with
        {
            DisplayName = data.DisplayName,
            Connections = connections,
            Aliases = data.Aliases is { Count: > 0 } aliases ? aliases : null,
        };
    }

    private static Dictionary<string, string> Without(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.Where(p => p.Key != key).ToDictionary(p => p.Key, p => p.Value);

    // Until the user signed in: who it is for (no connections yet).
    private static Account Placeholder(string cloudId, CloudAccountData data) =>
        new Account(Guid.NewGuid(), data.DisplayName, data.Email, []) with { CloudId = cloudId };

    // A signed-in account: names, reply address and signatures as the organisation set them.
    private async Task ApplyAsync(Account account, CloudAccountData data, CloudConnection connection, CancellationToken cancellationToken)
    {
        var updated = Identity(account, data);
        await accounts.SaveAccountAsync(updated, cancellationToken);
        await AssignSignaturesAsync(updated, data, connection, cancellationToken);
    }

    // The account's default signatures, when the organisation names them (its signatures, see CloudSignatureSync).
    private async Task AssignSignaturesAsync(Account account, CloudAccountData data, CloudConnection connection, CancellationToken cancellationToken)
    {
        if (data.SignatureNew is null && data.SignatureReply is null)
        {
            return;
        }

        await Mail.SignatureService.AssignAsync(settings, account.Id, new Mail.SignatureAssignment(
            data.SignatureNew is { } forNew ? CloudSignatureSync.LocalId(forNew, connection) : null,
            data.SignatureReply is { } forReply ? CloudSignatureSync.LocalId(forReply, connection) : null), cancellationToken);
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

/// <summary>
/// The account as the portal encrypted it (Portal → E-Mail-Konten, public/js/neruna-certificates.js, encryptAccount).
/// Which fields come depends on <see cref="Kind"/> (absent: imap) – see the contract, /api/v1/mail-accounts.
/// </summary>
public sealed record CloudAccountData(
    string DisplayName,
    string Email,
    string? Username = null,
    string? Password = null,
    IReadOnlyList<MailIdentity>? Aliases = null,
    CloudServer? Imap = null,
    CloudServer? Smtp = null,
    string? CaldavUrl = null,
    string? CarddavUrl = null,
    string? ReplyTo = null,
    string? Kind = null,
    CloudEws? Ews = null,
    string? NextcloudUrl = null,
    string? SignatureNew = null,
    string? SignatureReply = null);

public sealed record CloudEws(string? Url, string? Authentication);

public sealed record CloudServer(string Host, int Port, string? Security);
