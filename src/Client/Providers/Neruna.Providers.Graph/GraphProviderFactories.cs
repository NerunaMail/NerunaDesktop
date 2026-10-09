using Microsoft.Extensions.Logging;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Auth;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;

namespace Neruna.Providers.Graph;

/// <summary>Shared by the three Graph factories: one sign-in (token) serves mail, calendar and contacts of an account.</summary>
public sealed class GraphConnectionFactory(HttpClient http, ICredentialStore credentials)
{
    private readonly OAuthClient _oauth = new(http);

    internal GraphClient ClientFor(ServiceConnection connection) =>
        new(http, new OAuthTokenSource(_oauth, MicrosoftAccount.Endpoints, credentials, GraphSettings.FromDictionary(connection.Settings).TokenId));

    /// <summary>The sign-in in the browser; the tokens are stored under <paramref name="tokenId"/> (keychain only).</summary>
    public async Task SignInAsync(Guid tokenId, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        var tokens = await _oauth.SignInAsync(MicrosoftAccount.Endpoints, loginHint, browser, cancellationToken);
        await OAuthTokenSource.StoreAsync(credentials, tokenId, tokens, cancellationToken);
    }

    /// <summary>Who signed in (address and name from Graph), for the new account.</summary>
    public async Task<(string Email, string DisplayName)> WhoAmIAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var client = new GraphClient(http, new OAuthTokenSource(_oauth, MicrosoftAccount.Endpoints, credentials, tokenId));
        var me = await client.GetAsync("me?$select=displayName,mail,userPrincipalName", cancellationToken);
        return (me.Str("mail") ?? me.Str("userPrincipalName") ?? string.Empty, me.Str("displayName") ?? string.Empty);
    }

    /// <summary>The three connections of a Microsoft account, all signed in with the same token.</summary>
    public static IReadOnlyList<ServiceConnection> Connections(string user, Guid tokenId)
    {
        var settings = new GraphSettings(user, tokenId).ToDictionary();
        return [.. new[] { ServiceKind.Mail, ServiceKind.Calendar, ServiceKind.Contacts }
            .Select(kind => new ServiceConnection(kind == ServiceKind.Mail ? tokenId : Guid.NewGuid(), kind, ProviderIds.Graph, settings))];
    }
}

public sealed class GraphMailProviderFactory(GraphConnectionFactory connections, ILoggerFactory loggerFactory) : IProviderFactory<IMailProvider>
{
    public string ProviderId => ProviderIds.Graph;

    public string DisplayName => "Microsoft 365";

    public IMailProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new GraphMailProvider(connection.Id, connections.ClientFor(connection), loggerFactory.CreateLogger<GraphMailProvider>());
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) => null;
}

public sealed class GraphCalendarProviderFactory(GraphConnectionFactory connections) : IProviderFactory<ICalendarProvider>
{
    public string ProviderId => ProviderIds.Graph;

    public string DisplayName => "Microsoft 365";

    public ICalendarProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new GraphCalendarProvider(connection.Id, connections.ClientFor(connection));
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) => null;
}

public sealed class GraphContactProviderFactory(GraphConnectionFactory connections) : IProviderFactory<IContactProvider>
{
    public string ProviderId => ProviderIds.Graph;

    public string DisplayName => "Microsoft 365";

    public IContactProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new GraphContactProvider(connection.Id, connections.ClientFor(connection));
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) => null;
}
