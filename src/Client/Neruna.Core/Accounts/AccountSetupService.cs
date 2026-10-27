using Neruna.Contracts.Discovery;
using Neruna.Core.Auth;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Accounts;

/// <summary>
/// Turns a discovered configuration into an account. Every registered provider is asked which part of the
/// configuration it can handle, so adding e.g. a CalDAV provider makes new accounts include calendars automatically.
/// </summary>
public sealed class AccountSetupService(ProviderRegistry providers, IAccountStore accounts, ICredentialStore credentials, IEnumerable<IAccountSignIn> signIns)
{
    /// <summary>
    /// An existing account as servers, for "Konto bearbeiten": what each connection's provider can describe (the first
    /// connection per kind – the ones setup made from the discovered configuration).
    /// </summary>
    public MailProviderConfig Describe(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var incoming = new List<MailServerSettings>();
        var outgoing = new List<MailServerSettings>();
        var dav = new List<DavServerSettings>();
        foreach (var connection in EditableConnections(account))
        {
            if (Describe(connection) is { } part)
            {
                incoming.AddRange(part.IncomingServers);
                outgoing.AddRange(part.OutgoingServers);
                dav.AddRange(part.DavServers);
            }
        }

        var domain = account.EmailAddress?.Split('@').LastOrDefault() ?? string.Empty;
        return new MailProviderConfig(domain, null, incoming, outgoing, dav);
    }

    /// <summary>
    /// Saves changed names and servers of an account. Each connection keeps its id (and so its offline data); a service
    /// left empty is removed, a new one added. Every connection is verified first; if one fails, nothing changes –
    /// not even the passwords.
    /// </summary>
    /// <param name="password">A new password for all connections, or null to keep the stored ones.</param>
    /// <exception cref="AccountSetupException">A connection could not be established; nothing is saved.</exception>
    /// <param name="acceptAnyCertificate">Accept any server certificate (test servers); ignored for accounts from Neruna Cloud.</param>
    /// <param name="options">Further settings for every connection (see <see cref="BuildAccount"/>).</param>
    public async Task<Account> UpdateAsync(Account account, string displayName, string? label, string emailAddress, MailProviderConfig config, string? password, bool acceptAnyCertificate = false,
        IReadOnlyDictionary<string, string>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var built = BuildAccount(displayName, emailAddress, config, acceptAnyCertificate && !account.IsFromCloud, options);
        var editable = EditableConnections(account).ToList();
        var connections = account.Connections.Where(c => !editable.Contains(c)).ToList();
        var added = new List<ServiceConnection>();
        foreach (var connection in built.Connections)
        {
            // Same service and provider as before: same id, so folders, mails and calendars stay.
            if (editable.FirstOrDefault(c => c.Kind == connection.Kind && c.ProviderId == connection.ProviderId) is { } existing)
            {
                // The organisation's Antwortadresse stays with the connection until the next cloud sync sets it.
                var kept = existing.Settings.GetValueOrDefault(Mail.ReplyAddress.CloudSetting) is { Length: > 0 } replyTo
                    ? new Dictionary<string, string>(connection.Settings) { [Mail.ReplyAddress.CloudSetting] = replyTo }
                    : connection.Settings;
                connections.Add(connection with { Id = existing.Id, Settings = kept });
            }
            else
            {
                connections.Add(connection);
                added.Add(connection);
            }
        }

        // Secrets: a new password for all, else new connections get the one the account already uses.
        var previous = new Dictionary<Guid, string?>();
        var known = editable.Count > 0 ? await credentials.GetSecretAsync(editable[0].Id, cancellationToken) : null;
        foreach (var connection in connections.Where(c => built.Connections.Any(b => b.Kind == c.Kind && b.ProviderId == c.ProviderId)))
        {
            previous[connection.Id] = await credentials.GetSecretAsync(connection.Id, cancellationToken);
            var secret = password ?? previous[connection.Id] ?? known;
            if (secret is not null)
            {
                await credentials.SetSecretAsync(connection.Id, secret, cancellationToken);
            }
        }

        // Aliases and the cloud id come with the passed account.
        var updated = account with { DisplayName = displayName, EmailAddress = emailAddress, Connections = connections, Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() };
        ServiceConnection? testing = null;
        try
        {
            foreach (var connection in connections.Where(c => previous.ContainsKey(c.Id)))
            {
                testing = connection;
                await TestAsync(connection, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var (id, secret) in previous)
            {
                if (secret is null)
                {
                    await credentials.DeleteSecretAsync(id, CancellationToken.None);
                }
                else
                {
                    await credentials.SetSecretAsync(id, secret, CancellationToken.None);
                }
            }

            throw new AccountSetupException($"{KindName(testing?.Kind)} ({testing?.ProviderId}): {ex.Message}", ex);
        }

        // Services left out are gone (their offline data with them); their passwords too.
        foreach (var removed in editable.Where(c => connections.All(n => n.Id != c.Id)))
        {
            await credentials.DeleteSecretAsync(removed.Id, cancellationToken);
        }

        await accounts.SaveAccountAsync(updated, cancellationToken);

        // Typed in for an account of the organisation that came without one: kept for it (and the personal backup).
        if (account.CloudId is { } cloudId && password is not null)
        {
            await credentials.SetSecretAsync(Cloud.CloudAccountPasswords.SecretId(cloudId), password, cancellationToken);
        }

        return updated;
    }

    // Per kind the first connection a provider can describe – what setup created; extra ones (ICS feeds …) stay as they are.
    private IEnumerable<ServiceConnection> EditableConnections(Account account) =>
        account.Connections.GroupBy(c => c.Kind).Select(g => g.FirstOrDefault(c => Describe(c) is not null)).OfType<ServiceConnection>();

    private MailProviderConfig? Describe(ServiceConnection connection)
    {
        try
        {
            return connection.Kind switch
            {
                ServiceKind.Mail => providers.MailProviders.FirstOrDefault(f => f.ProviderId == connection.ProviderId)?.DescribeSettings(connection.Settings),
                ServiceKind.Calendar => providers.CalendarProviders.FirstOrDefault(f => f.ProviderId == connection.ProviderId)?.DescribeSettings(connection.Settings),
                ServiceKind.Contacts => providers.ContactProviders.FirstOrDefault(f => f.ProviderId == connection.ProviderId)?.DescribeSettings(connection.Settings),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null; // incomplete settings: not editable here
        }
    }

    private static string KindName(ServiceKind? kind) => kind switch
    {
        ServiceKind.Mail => T("E-Mail"),
        ServiceKind.Calendar => T("Kalender"),
        ServiceKind.Contacts => T("Kontakte"),
        _ => T("Verbindung"),
    };

    /// <param name="acceptAnyCertificate">Every connection accepts any server certificate (test servers with a self-signed one).</param>
    /// <param name="options">
    /// Further settings for every connection, beyond what the configuration describes – e.g. a TLS client certificate
    /// (<see cref="Security.TlsCertificates.ClientCertificateKey"/>) or how to sign in at Exchange.
    /// </param>
    public Account BuildAccount(string displayName, string emailAddress, MailProviderConfig config, bool acceptAnyCertificate = false, IReadOnlyDictionary<string, string>? options = null)
    {
        var connections = new List<ServiceConnection>();
        AddConnections(connections, ServiceKind.Mail, providers.MailProviders, config, emailAddress);
        AddConnections(connections, ServiceKind.Calendar, providers.CalendarProviders, config, emailAddress);
        AddConnections(connections, ServiceKind.Contacts, providers.ContactProviders, config, emailAddress);
        var extra = new Dictionary<string, string>(options ?? new Dictionary<string, string>());
        if (acceptAnyCertificate)
        {
            extra[Security.TlsCertificates.AcceptAnyCertificateKey] = "true";
        }

        if (extra.Count > 0)
        {
            connections = [.. connections.Select(c => c with { Settings = new Dictionary<string, string>(c.Settings.Concat(extra).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)) })];
        }

        return new Account(Guid.NewGuid(), displayName, emailAddress, connections);
    }

    /// <summary>Stores the password for all connections, verifies them and saves the account.</summary>
    /// <param name="password">Null for connections that need no secret (e.g. public ICS feeds).</param>
    /// <exception cref="AccountSetupException">A connection could not be established; nothing is saved.</exception>
    public async Task CreateAsync(Account account, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (password is not null)
        {
            foreach (var connection in account.Connections)
            {
                await credentials.SetSecretAsync(connection.Id, password, cancellationToken);
            }
        }

        ServiceConnection? testing = null;
        try
        {
            foreach (var connection in account.Connections)
            {
                testing = connection;
                await TestAsync(connection, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var connection in account.Connections)
            {
                await credentials.DeleteSecretAsync(connection.Id, CancellationToken.None);
            }

            throw new AccountSetupException($"{KindName(testing?.Kind)} ({testing?.ProviderId}): {ex.Message}", ex);
        }

        await accounts.SaveAccountAsync(account, cancellationToken);
    }

    /// <summary>Names only (sender name, label) – for accounts whose servers Neruna does not edit (e.g. Microsoft 365).</summary>
    public async Task<Account> RenameAsync(Account account, string displayName, string? label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var renamed = account with { DisplayName = displayName, Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() };
        await accounts.SaveAccountAsync(renamed, cancellationToken);
        return renamed;
    }

    /// <summary>Whether accounts of this kind can be added by signing in (the build has the app registration).</summary>
    public bool CanSignIn(string providerId) => signIns.Any(s => s.ProviderId == providerId && s.IsAvailable);

    /// <summary>Whether the account was set up by signing in (its servers belong to the provider, only names are edited).</summary>
    public bool IsSignedIn(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Connections.Any(c => signIns.Any(s => s.ProviderId == c.ProviderId));
    }

    /// <summary>
    /// Signs in in the browser and sets up the account with the address and name the provider reports (or the
    /// given name); the connections are tested before it is saved.
    /// </summary>
    /// <exception cref="AccountSetupException">Sign-in or a connection failed; nothing is saved.</exception>
    public async Task<Account> CreateBySignInAsync(string providerId, string? displayName, string? label, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        var who = await SignIn(providerId).SignInAsync(null, loginHint, browser, cancellationToken);
        var account = new Account(Guid.NewGuid(), string.IsNullOrWhiteSpace(displayName) ? who.DisplayName : displayName.Trim(), who.EmailAddress,
            who.Connections, string.IsNullOrWhiteSpace(label) ? null : label.Trim());
        await CreateAsync(account, null, cancellationToken);
        return account;
    }

    /// <summary>
    /// Signs in again (password changed, new permissions) and keeps the account; services allowed at the provider since
    /// are added (tested first), withdrawn ones removed – the others keep their ids and offline data. Then the names.
    /// </summary>
    /// <exception cref="AccountSetupException">The sign-in failed, or a newly allowed service could not be reached.</exception>
    public async Task<Account> SignInAgainAsync(Account account, string displayName, string? label, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var provider = SignInOf(account) ?? throw new AccountSetupException(T("Dieses Konto wird nicht über eine Anmeldung im Browser verbunden."));
        var who = await provider.SignInAsync(account, loginHint, browser, cancellationToken);
        var added = who.Connections.Where(c => account.Connections.All(o => o.Id != c.Id)).ToList();
        if (added.Count > 0 || account.Connections.Any(o => who.Connections.All(c => c.Id != o.Id)))
        {
            foreach (var connection in added)
            {
                try
                {
                    await TestAsync(connection, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new AccountSetupException($"{KindName(connection.Kind)} ({connection.ProviderId}): {ex.Message}", ex);
                }
            }

            account = account with { Connections = who.Connections };
            await accounts.SaveAccountAsync(account, cancellationToken);
        }

        return await RenameAsync(account, displayName, label, cancellationToken);
    }

    /// <summary>Services of a signed-in account the user did not allow at the provider (names for the user); none if all are.</summary>
    public async Task<IReadOnlyList<string>> NotGrantedAsync(Account account, CancellationToken cancellationToken = default) =>
        SignInOf(account) is { } provider ? await provider.NotGrantedAsync(account, cancellationToken) : [];

    private IAccountSignIn? SignInOf(Account account) =>
        signIns.FirstOrDefault(s => account.Connections.Any(c => c.ProviderId == s.ProviderId));

    private IAccountSignIn SignIn(string providerId) =>
        signIns.FirstOrDefault(s => s.ProviderId == providerId && s.IsAvailable)
        ?? throw new AccountSetupException(T("Diese Anmeldung ist in dieser Version von Neruna noch nicht eingerichtet."));

    /// <summary>One line about a connection's server (host, port, user; URL), as the provider describes it.</summary>
    public string Summary(ServiceConnection connection) => providers.Summary(connection);

    /// <summary>
    /// The services this installation can set up (e.g. "calendar:caldav"). Changes when an update brings a new one –
    /// accounts described by others (the organisation) are then built again to get it.
    /// </summary>
    public string Capabilities => string.Join(",",
        providers.MailProviders.Select(p => "mail:" + p.ProviderId)
            .Concat(providers.CalendarProviders.Select(p => "calendar:" + p.ProviderId))
            .Concat(providers.ContactProviders.Select(p => "contacts:" + p.ProviderId))
            .Order(StringComparer.Ordinal));

    private async Task TestAsync(ServiceConnection connection, CancellationToken cancellationToken)
    {
        switch (connection.Kind)
        {
            case ServiceKind.Mail:
                await using (var mail = providers.CreateMail(connection))
                {
                    await mail.TestConnectionAsync(cancellationToken);
                }

                break;
            case ServiceKind.Calendar:
                await using (var calendar = providers.CreateCalendar(connection))
                {
                    await calendar.TestConnectionAsync(cancellationToken);
                }

                break;
            case ServiceKind.Contacts:
                await using (var contacts = providers.CreateContacts(connection))
                {
                    await contacts.TestConnectionAsync(cancellationToken);
                }

                break;
        }
    }

    private static void AddConnections<T>(List<ServiceConnection> target, ServiceKind kind, IEnumerable<IProviderFactory<T>> factories, MailProviderConfig config, string emailAddress)
    {
        foreach (var factory in factories)
        {
            if (factory.SettingsFromDiscovery(config, emailAddress) is { } settings)
            {
                target.Add(new ServiceConnection(Guid.NewGuid(), kind, factory.ProviderId, settings));
                return; // one connection per kind from discovery; more can be added manually
            }
        }
    }
}

public sealed class AccountSetupException : Exception
{
    public AccountSetupException()
    {
    }

    public AccountSetupException(string message)
        : base(message)
    {
    }

    public AccountSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
