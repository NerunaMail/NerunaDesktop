using Neruna.Contracts.Discovery;
using Neruna.Core.Providers;
using Neruna.Core.Security;

namespace Neruna.Core.Accounts;

/// <summary>
/// Turns a discovered configuration into an account. Every registered provider is asked which part of the
/// configuration it can handle, so adding e.g. a CalDAV provider makes new accounts include calendars automatically.
/// </summary>
public sealed class AccountSetupService(ProviderRegistry providers, IAccountStore accounts, ICredentialStore credentials)
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
    public async Task<Account> UpdateAsync(Account account, string displayName, string? label, string emailAddress, MailProviderConfig config, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var built = BuildAccount(displayName, emailAddress, config);
        var editable = EditableConnections(account).ToList();
        var connections = account.Connections.Where(c => !editable.Contains(c)).ToList();
        var added = new List<ServiceConnection>();
        foreach (var connection in built.Connections)
        {
            // Same service and provider as before: same id, so folders, mails and calendars stay.
            if (editable.FirstOrDefault(c => c.Kind == connection.Kind && c.ProviderId == connection.ProviderId) is { } existing)
            {
                connections.Add(connection with { Id = existing.Id });
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
        ServiceKind.Mail => "E-Mail",
        ServiceKind.Calendar => "Kalender",
        ServiceKind.Contacts => "Kontakte",
        _ => "Verbindung",
    };

    public Account BuildAccount(string displayName, string emailAddress, MailProviderConfig config)
    {
        var connections = new List<ServiceConnection>();
        AddConnections(connections, ServiceKind.Mail, providers.MailProviders, config, emailAddress);
        AddConnections(connections, ServiceKind.Calendar, providers.CalendarProviders, config, emailAddress);
        AddConnections(connections, ServiceKind.Contacts, providers.ContactProviders, config, emailAddress);

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
