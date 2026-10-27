using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Auth;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;
using Neruna.Providers.Imap;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Google;

/// <summary>
/// Shared by the three Google factories: one sign-in (token, keychain only) serves Gmail, the calendar with the tasks
/// and the contacts. Nothing goes through Neruna Cloud.
/// </summary>
public sealed class GoogleConnectionFactory(HttpClient http, ICredentialStore credentials)
{
    private readonly OAuthClient _oauth = new(http);

    internal OAuthTokenSource TokensFor(Guid tokenId) => new(_oauth, GoogleAccount.Endpoints, credentials, tokenId);

    internal Func<CancellationToken, Task<string>> AccessTokenFor(ServiceConnection connection)
    {
        var tokens = TokensFor(GoogleSettings.FromDictionary(connection.Settings).TokenId);
        return tokens.GetAccessTokenAsync;
    }

    /// <summary>The sign-in in the browser; the tokens are stored under <paramref name="tokenId"/> (keychain only).</summary>
    public async Task SignInAsync(Guid tokenId, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        var tokens = await _oauth.SignInAsync(GoogleAccount.Endpoints, loginHint, browser, cancellationToken);
        await OAuthTokenSource.StoreAsync(credentials, tokenId, tokens, cancellationToken);
    }

    /// <summary>Who signed in (address and name), for the new account.</summary>
    public async Task<(string Email, string DisplayName)> WhoAmIAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GoogleAccount.UserInfo);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokensFor(tokenId).GetAccessTokenAsync(cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var me = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        string? Str(string name) => me.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return (Str("email") ?? string.Empty, Str("name") ?? string.Empty);
    }

    /// <summary>The three connections of a Google account, all signed in with the same token.</summary>
    public static IReadOnlyList<ServiceConnection> Connections(string user, Guid tokenId)
    {
        var settings = new GoogleSettings(user, tokenId).ToDictionary();
        return
        [
            new ServiceConnection(tokenId, ServiceKind.Mail, ProviderIds.Google, settings),
            new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.GoogleCalendar, settings),
            new ServiceConnection(Guid.NewGuid(), ServiceKind.Contacts, ProviderIds.GoogleContacts, settings),
        ];
    }
}

/// <summary>Google: sign-in in the browser, then Gmail, calendar with tasks and contacts with one token.</summary>
public sealed class GoogleSignIn(GoogleConnectionFactory google) : IAccountSignIn
{
    public string ProviderId => ProviderIds.Google;

    public bool IsAvailable => GoogleAccount.IsConfigured;

    public async Task<SignedInAccount> SignInAsync(Account? existing, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        // Signing in again keeps the token id, so the account's connections stay as they are.
        var tokenId = existing?.Connections.Where(c => c.ProviderId == ProviderIds.Google)
                          .Select(c => (Guid?)GoogleSettings.FromDictionary(c.Settings).TokenId).FirstOrDefault()
                      ?? Guid.NewGuid();
        try
        {
            await google.SignInAsync(tokenId, loginHint, browser, cancellationToken);
            var (email, name) = await google.WhoAmIAsync(tokenId, cancellationToken);
            return new SignedInAccount(email, name, existing?.Connections ?? GoogleConnectionFactory.Connections(email, tokenId));
        }
        catch (OAuthException ex) when (ex.Message.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
        {
            // Also what Google answers while the app is in testing and the address is not one of its testers.
            throw new AccountSetupException(T("Google hat die Anmeldung abgelehnt. Solange Neruna bei Google in der Testphase ist, klappt sie nur mit freigeschalteten Adressen – senden Sie Ihre Gmail-Adresse an info@neruna.org."), ex);
        }
        catch (Exception ex) when (ex is OAuthException or HttpRequestException)
        {
            throw new AccountSetupException(ex.Message, ex);
        }
    }
}

public sealed class GoogleMailProviderFactory(GoogleConnectionFactory google, ICredentialStore credentials, ILoggerFactory loggerFactory) : IProviderFactory<IMailProvider>
{
    public string ProviderId => ProviderIds.Google;

    public string DisplayName => "Google";

    public IMailProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ImapMailProvider(connection.Id, Settings(connection.Settings), credentials, loggerFactory.CreateLogger<ImapMailProvider>())
        {
            AccessToken = google.AccessTokenFor(connection),
        };
    }

    /// <summary>Gmail's servers; Gmail files sent mail in "Gesendet" itself (a copy from Neruna would be a second one).</summary>
    internal static ImapSettings Settings(IReadOnlyDictionary<string, string> settings)
    {
        var user = GoogleSettings.FromDictionary(settings).User;
        var imap = new ImapSettings(GoogleAccount.ImapHost, 993, SocketSecurity.SslOnConnect, GoogleAccount.SmtpHost, 465, SocketSecurity.SslOnConnect, user);
        return imap with { Raw = new Dictionary<string, string>(imap.ToDictionary()) { [ImapSettings.ServerSavesSentKey] = "true" } };
    }

    public string Summary(IReadOnlyDictionary<string, string> settings) => $"Google · {GoogleSettings.FromDictionary(settings).User}";
}

public sealed class GoogleCalendarProviderFactory(GoogleConnectionFactory google, HttpClient http, ICredentialStore credentials, ILoggerFactory loggerFactory) : IProviderFactory<ICalendarProvider>
{
    public string ProviderId => ProviderIds.GoogleCalendar;

    public string DisplayName => "Google";

    public ICalendarProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var user = GoogleSettings.FromDictionary(connection.Settings).User;
        var token = google.AccessTokenFor(connection);
        var tokens = google.TokensFor(GoogleSettings.FromDictionary(connection.Settings).TokenId);
        var dav = DavOAuthProviders.Calendar(connection.Id, new DavSettings(GoogleAccount.CalDav(user), user), http, credentials, token, loggerFactory.CreateLogger<GoogleCalendarProvider>());
        return new GoogleCalendarProvider(connection.Id, dav, new GoogleTasks(http, token), ct => tokens.GrantsAsync(GoogleAccount.TasksScope, ct));
    }

    public string Summary(IReadOnlyDictionary<string, string> settings) => $"Google · {GoogleSettings.FromDictionary(settings).User}";
}

public sealed class GoogleContactProviderFactory(GoogleConnectionFactory google, HttpClient http, ICredentialStore credentials, ILoggerFactory loggerFactory) : IProviderFactory<IContactProvider>
{
    public string ProviderId => ProviderIds.GoogleContacts;

    public string DisplayName => "Google";

    public IContactProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var user = GoogleSettings.FromDictionary(connection.Settings).User;
        return DavOAuthProviders.Contacts(connection.Id, new DavSettings(GoogleAccount.CardDav, user), http, credentials, google.AccessTokenFor(connection),
            loggerFactory.CreateLogger<GoogleContactProviderFactory>());
    }

    public string Summary(IReadOnlyDictionary<string, string> settings) => $"Google · {GoogleSettings.FromDictionary(settings).User}";
}
