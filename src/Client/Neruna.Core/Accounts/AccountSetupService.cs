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

            var what = testing?.Kind switch
            {
                ServiceKind.Mail => "E-Mail",
                ServiceKind.Calendar => "Kalender",
                ServiceKind.Contacts => "Kontakte",
                _ => "Verbindung",
            };
            throw new AccountSetupException($"{what} ({testing?.ProviderId}): {ex.Message}", ex);
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
