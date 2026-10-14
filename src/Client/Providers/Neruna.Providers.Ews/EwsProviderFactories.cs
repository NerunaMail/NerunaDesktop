using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;

namespace Neruna.Providers.Ews;

/// <summary>
/// Shared by the three EWS factories: one HTTP client per connection, with its password (Negotiate/NTLM/Basic as the
/// server offers), its client certificate (for a reverse proxy that demands one) and its server certificate check.
/// A client lives as long as these stay the same – NTLM signs in per TCP connection, a new client would do it again.
/// </summary>
public sealed class EwsConnectionFactory(ICredentialStore credentials, ClientCertificates clientCertificates, ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<Guid, (string Key, HttpClient Client)> _clients = new();

    /// <summary>For tests: the handler every client gets instead of a real one.</summary>
    internal Func<HttpMessageHandler>? HandlerOverride { get; init; }

    internal async Task<EwsClient> ClientForAsync(ServiceConnection connection, CancellationToken cancellationToken)
    {
        var settings = EwsSettings.FromDictionary(connection.Settings);
        var password = settings.Authentication == EwsAuthentication.CertificateOnly
            ? null
            : await credentials.GetSecretAsync(connection.Id, cancellationToken);
        var certificate = TlsCertificates.ClientCertificateOf(connection.Settings);
        var acceptAny = TlsCertificates.AcceptsAnyCertificate(connection.Settings);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', settings.Url, settings.Username, settings.Authentication, password, certificate, acceptAny))));

        // One client per password (hashed) and settings: changed ones get a new client, the old one is closed.
        if (!_clients.TryGetValue(connection.Id, out var cached) || cached.Key != key)
        {
            var handler = HandlerOverride?.Invoke() ?? await CreateHandlerAsync(settings, password, certificate, acceptAny, cancellationToken);
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            _clients[connection.Id] = (key, client);
            cached.Client?.Dispose();
            cached = (key, client);
        }

        return new EwsClient(cached.Client, settings, password, loggerFactory.CreateLogger<EwsClient>());
    }

    private async Task<HttpMessageHandler> CreateHandlerAsync(EwsSettings settings, string? password, string? certificateReference, bool acceptAny, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            // EWS does not redirect; a redirect would take the password elsewhere.
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            SslOptions = { RemoteCertificateValidationCallback = TlsCertificates.For(acceptAny) },
        };

        if (settings.Authentication == EwsAuthentication.Automatic && password is not null)
        {
            // "EXAMPLE\anna" signs in at the domain EXAMPLE; "anna@example.com" as it is (UPN).
            var parts = settings.Username.Split('\\', 2);
            var credential = parts.Length == 2 ? new NetworkCredential(parts[1], password, parts[0]) : new NetworkCredential(settings.Username, password);

            // Only to the EWS host, never to another one.
            var cache = new CredentialCache();
            var origin = new Uri(settings.Url.GetLeftPart(UriPartial.Authority));
            foreach (var scheme in new[] { "Negotiate", "NTLM", "Basic" })
            {
                cache.Add(origin, scheme, credential);
            }

            handler.Credentials = cache;
        }

        if (certificateReference is not null)
        {
            var certificate = await clientCertificates.LoadAsync(certificateReference, cancellationToken)
                              ?? throw new EwsException("ClientCertificate", Core.Localization.Texts.T("Das gewählte Client-Zertifikat ist auf diesem Gerät nicht vorhanden. Bitte im Konto erneut wählen."));
            handler.SslOptions.ClientCertificates = [certificate];

            // Shown whenever the server asks – also when its list of accepted issuers does not name ours (intermediate CAs).
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        return handler;
    }

    /// <summary>The settings of a connection, for the account dialog.</summary>
    public static EwsSettings SettingsOf(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return EwsSettings.FromDictionary(connection.Settings);
    }

    internal ILogger<T> Logger<T>() => loggerFactory.CreateLogger<T>();
}

public sealed class EwsMailProviderFactory(EwsConnectionFactory connections) : IProviderFactory<IMailProvider>
{
    public string ProviderId => ProviderIds.Ews;

    public string DisplayName => "Microsoft Exchange (EWS)";

    public IMailProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new EwsMailProvider(connection, connections);
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) =>
        EwsSettings.FromDiscovery(config, emailAddress)?.ToDictionary();

    public MailProviderConfig? DescribeSettings(IReadOnlyDictionary<string, string> settings) => Describe(settings);

    public string Summary(IReadOnlyDictionary<string, string> settings) => SummaryOf(settings);

    internal static MailProviderConfig Describe(IReadOnlyDictionary<string, string> settings)
    {
        var ews = EwsSettings.FromDictionary(settings);
        return new MailProviderConfig(string.Empty, null, [], [], [new DavServerSettings(ServerProtocol.Ews, ews.Url, ews.Username)]);
    }

    internal static string SummaryOf(IReadOnlyDictionary<string, string> settings)
    {
        var ews = EwsSettings.FromDictionary(settings);
        return $"{ews.Url.Host} (EWS) · {ews.Username}" + (TlsCertificates.ClientCertificateOf(settings) is null ? string.Empty : " · " + Core.Localization.Texts.T("Client-Zertifikat"));
    }
}

public sealed class EwsCalendarProviderFactory(EwsConnectionFactory connections) : IProviderFactory<ICalendarProvider>
{
    public string ProviderId => ProviderIds.Ews;

    public string DisplayName => "Microsoft Exchange (EWS)";

    public ICalendarProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new EwsCalendarProvider(connection, connections);
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) =>
        EwsSettings.FromDiscovery(config, emailAddress)?.ToDictionary();

    public MailProviderConfig? DescribeSettings(IReadOnlyDictionary<string, string> settings) => EwsMailProviderFactory.Describe(settings);

    public string Summary(IReadOnlyDictionary<string, string> settings) => EwsMailProviderFactory.SummaryOf(settings);
}

public sealed class EwsContactProviderFactory(EwsConnectionFactory connections) : IProviderFactory<IContactProvider>
{
    public string ProviderId => ProviderIds.Ews;

    public string DisplayName => "Microsoft Exchange (EWS)";

    public IContactProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new EwsContactProvider(connection, connections);
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) =>
        EwsSettings.FromDiscovery(config, emailAddress)?.ToDictionary();

    public MailProviderConfig? DescribeSettings(IReadOnlyDictionary<string, string> settings) => EwsMailProviderFactory.Describe(settings);

    public string Summary(IReadOnlyDictionary<string, string> settings) => EwsMailProviderFactory.SummaryOf(settings);
}
