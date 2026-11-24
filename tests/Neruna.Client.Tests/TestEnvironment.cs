using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Providers.Ics;
using Neruna.Storage;

namespace Neruna.Client.Tests;

/// <summary>Real SQLite storage in a temp directory, real controllers, fake or stubbed providers.</summary>
internal sealed class TestEnvironment : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private TestEnvironment(ServiceProvider services, string directory)
    {
        _services = services;
        Directory = directory;
    }

    public string Directory { get; }

    public FakeMailServer MailServer { get; } = new();

    public StubHttpHandler Http { get; } = new();

    public IAccountStore Accounts => _services.GetRequiredService<IAccountStore>();

    public IMailStore MailStore => _services.GetRequiredService<IMailStore>();

    public MailController Mail => _services.GetRequiredService<MailController>();

    public CalendarController Calendar => _services.GetRequiredService<CalendarController>();

    public TaskController Tasks => _services.GetRequiredService<TaskController>();

    public ContactController Contacts => _services.GetRequiredService<ContactController>();

    public ProviderRegistry Providers => _services.GetRequiredService<ProviderRegistry>();

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public static async Task<TestEnvironment> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        TestEnvironment? env = null;

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddNerunaStorage(directory);
        services.AddSingleton<IProviderFactory<IMailProvider>>(_ => new FakeMailProviderFactory(env!.MailServer));
        services.AddSingleton<IProviderFactory<ICalendarProvider>>(_ => new IcsProviderFactory(new HttpClient(env!.Http)));
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton(new LocalDraftStore(directory));
        services.AddSingleton<MailController>();
        services.AddSingleton<MailPushService>();
        services.AddSingleton<Neruna.Core.Calendar.ReminderService>();
        services.AddSingleton<Neruna.Core.Calendar.InvitationService>();
        services.AddSingleton<CalendarController>();
        services.AddSingleton<TaskController>();
        services.AddSingleton<SyncCoordinator>();
        services.AddSingleton<ContactController>();
        services.AddSingleton<Neruna.Core.Notes.NoteController>();
        services.AddSingleton<AccountSetupService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Neruna.Core.Security.CertificateManager>();
        services.AddSingleton<Neruna.Core.Security.PgpKeyManager>();
        services.AddSingleton(_ => new Neruna.Core.Security.PgpKeyLookup(new HttpClient(env!.Http), NullLogger<Neruna.Core.Security.PgpKeyLookup>.Instance));
        services.AddSingleton<Neruna.Core.Security.SecureMimeService>();
        services.AddSingleton<SignatureService>();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        env = new TestEnvironment(provider, directory);
        await provider.InitializeNerunaStorageAsync();
        return env;
    }

    public async Task<ServiceConnection> AddAccountAsync(ServiceKind kind, string providerId, IReadOnlyDictionary<string, string>? settings = null)
    {
        var connection = new ServiceConnection(Guid.NewGuid(), kind, providerId, settings ?? new Dictionary<string, string>());
        await Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Test", "anna@example.com", [connection]));
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

/// <summary>Answers HTTP requests from a URL → (status, body) table.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    /// <summary>Every server shows a valid certificate (signed with the test key) unless set here or in <see cref="Responses"/>.</summary>
    public bool NoServerCertificate { get; set; }

    public List<string> UserAgents { get; } = [];

    /// <summary>The Authorization header of each request ("" without one).</summary>
    public List<string> Authorizations { get; } = [];

    /// <summary>Method, address and body of requests that had one (POST, PUT).</summary>
    public List<(string Method, string Url, string Body)> Sent { get; } = [];

    /// <summary>Addresses whose body went without a Content-Length (chunked – the hosting drops large ones).</summary>
    public List<string> Chunked { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requested.Add(url);
        UserAgents.Add(request.Headers.UserAgent.ToString());
        Authorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
        if (request.Content is not null)
        {
            if (request.Content.Headers.ContentLength is null)
            {
                Chunked.Add(url);
            }

            Sent.Add((request.Method.Method, url, await request.Content.ReadAsStringAsync(cancellationToken)));
        }

        var (status, body) = Responses.TryGetValue(url, out var r) ? r
            : url.EndsWith("/api/v1/server-certificate", StringComparison.Ordinal) && !NoServerCertificate
                ? (HttpStatusCode.OK, TestServerCertificate.For([request.RequestUri.Host]))
                : (HttpStatusCode.NotFound, string.Empty);
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

/// <summary>
/// Server certificates as Neruna signs them, with the test key of the contract fixture (seed 32 × 0x2a) – trusted by
/// the apps in this test run instead of Neruna's.
/// </summary>
internal static class TestServerCertificate
{
    private static readonly Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters Key = new(Enumerable.Repeat((byte)0x2a, 32).ToArray(), 0);

    public static byte[] PublicKey => Key.GeneratePublicKey().GetEncoded();

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void TrustTestKey() => Neruna.Core.Cloud.ServerCertificate.TrustedKey = PublicKey;

    public static string For(IEnumerable<string> hosts, DateTimeOffset? expires = null)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["v"] = 1,
            ["instance"] = "test",
            ["hosts"] = hosts.ToArray(),
            ["issued_at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["expires_at"] = (expires ?? DateTimeOffset.UtcNow.AddMonths(6)).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, Key);
        signer.BlockUpdate(data, 0, data.Length);
        return System.Text.Json.JsonSerializer.Serialize(new { data = Convert.ToBase64String(data), signature = Convert.ToBase64String(signer.GenerateSignature()) });
    }
}
