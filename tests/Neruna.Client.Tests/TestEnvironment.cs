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
        services.AddSingleton<MailController>();
        services.AddSingleton<MailPushService>();
        services.AddSingleton<Neruna.Core.Calendar.ReminderService>();
        services.AddSingleton<Neruna.Core.Calendar.InvitationService>();
        services.AddSingleton<CalendarController>();
        services.AddSingleton<TaskController>();
        services.AddSingleton<SyncCoordinator>();
        services.AddSingleton<ContactController>();
        services.AddSingleton<AccountSetupService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Neruna.Core.Security.CertificateManager>();
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

    /// <summary>Method, address and body of requests that had one (POST, PUT).</summary>
    public List<(string Method, string Url, string Body)> Sent { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requested.Add(url);
        if (request.Content is not null)
        {
            Sent.Add((request.Method.Method, url, await request.Content.ReadAsStringAsync(cancellationToken)));
        }

        var (status, body) = Responses.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, string.Empty);
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
