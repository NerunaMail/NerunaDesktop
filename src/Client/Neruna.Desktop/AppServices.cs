using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Discovery;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;
using Neruna.Providers.Dav;
using Neruna.Providers.Demo;
using Neruna.Providers.Ics;
using Neruna.Providers.Imap;
using Neruna.Storage;

namespace Neruna.Desktop;

/// <param name="Demo">Start with the in-memory demo account in a separate data directory.</param>
/// <param name="ProtocolLog">Write raw IMAP/SMTP traces and DAV request/response bodies (passwords redacted).</param>
internal sealed record AppOptions(string DataDirectory, bool Demo, LogLevel LogLevel = LogLevel.Information, bool ProtocolLog = false)
{
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static AppOptions FromArgs(string[] args)
    {
        var demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var root = Environment.GetEnvironmentVariable("NERUNA_DATA_DIR")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Neruna");
        var protocolLog = Environment.GetEnvironmentVariable("NERUNA_PROTOCOL_LOG") is "1" or "true"
                          || args.Contains("--protocol-log", StringComparer.OrdinalIgnoreCase);
        var level = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("NERUNA_LOG_LEVEL"), ignoreCase: true, out var parsed)
            ? parsed
            : protocolLog ? LogLevel.Trace : LogLevel.Information;

        return new AppOptions(demo ? Path.Combine(root, "demo") : root, demo, level, protocolLog);
    }
}

/// <summary>Composition root: the only place that knows which providers exist.</summary>
internal static class AppServices
{
    public static async Task<ServiceProvider> BuildAsync(AppOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(options.LogLevel);
            logging.AddProvider(new FileLoggerProvider(options.LogDirectory, options.LogLevel));
        });

        services.AddHttpClient("neruna");
        services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("neruna"));

        // WebDAV clients must see redirects themselves to keep credentials (see DavClient).
        services.AddHttpClient("dav").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddNerunaStorage(options.DataDirectory, useSystemCredentials: true);

        // Providers. New protocols (EWS, JMAP, LDAP …) are added here and nowhere else.
        services.AddSingleton<IProviderFactory<IMailProvider>>(sp => new ImapProviderFactory(
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<ILoggerFactory>(),
            options.ProtocolLog ? Path.Combine(options.LogDirectory, "protocol") : null));
        services.AddSingleton<IProviderFactory<ICalendarProvider>>(sp => new CalDavProviderFactory(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("dav"), sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IProviderFactory<IContactProvider>>(sp => new CardDavProviderFactory(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("dav"), sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IProviderFactory<ICalendarProvider>, IcsProviderFactory>();
        if (options.Demo)
        {
            services.AddSingleton<IProviderFactory<IMailProvider>, DemoMailProviderFactory>();
            services.AddSingleton<IProviderFactory<ICalendarProvider>, DemoCalendarProviderFactory>();
            services.AddSingleton<IProviderFactory<IContactProvider>, DemoContactProviderFactory>();
        }

        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<MailController>();
        services.AddSingleton<MailPushService>();
        services.AddSingleton<Neruna.Core.Calendar.ReminderService>();
        services.AddSingleton<Neruna.Core.Calendar.InvitationService>();
        services.AddSingleton<SignatureService>();
        services.AddSingleton<CalendarController>();
        services.AddSingleton<ContactController>();
        services.AddSingleton<AccountSetupService>();
        services.AddSingleton<AccountDiscovery>();
        services.AddSingleton<Neruna.Core.Cloud.CloudController>();
        services.AddSingleton<Neruna.Core.Cloud.CloudSignatureSync>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<UiLayout>();
        services.AddSingleton<UiPreferences>();
        services.AddSingleton<RecipientDirectory>();
        services.AddSingleton<AgendaViewModel>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<ReminderScheduler>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CertificateManager>();
        services.AddSingleton<SecureMimeService>();

        services.AddSingleton<MailViewModel>();
        services.AddSingleton<CalendarViewModel>();
        services.AddSingleton<ContactsViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<CertificatesViewModel>();
        services.AddSingleton<MailOptionsViewModel>();
        services.AddSingleton<CalendarOptionsViewModel>();
        services.AddSingleton<SignaturesViewModel>();
        services.AddSingleton<AppearanceViewModel>();
        services.AddSingleton<CloudViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        await provider.InitializeNerunaStorageAsync();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Neruna");
        logger.LogInformation("Neruna Desktop {Version} started (data: {DataDirectory}, demo: {Demo}, protocol log: {ProtocolLog})",
            typeof(AppServices).Assembly.GetName().Version, options.DataDirectory, options.Demo, options.ProtocolLog);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        if (options.Demo)
        {
            await provider.GetRequiredService<IAccountStore>().SaveAccountAsync(DemoData.CreateAccount());
        }

        return provider;
    }
}
