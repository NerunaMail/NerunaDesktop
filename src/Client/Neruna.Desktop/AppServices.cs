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
        var root = Environment.GetEnvironmentVariable("NERUNA_DATA_DIR") ?? Neruna.Storage.DataFolder.Default();
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

        // Every client: "Neruna-Desktop/x.y" towards neruna.org/.cloud only, and 429/503 logged in detail.
        var userAgent = "Neruna-Desktop/" + (typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0");
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler(sp =>
            new Neruna.Core.Diagnostics.NerunaHttpHandler(userAgent, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Http"))));
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
        // Calendars Neruna computes itself: birthdays from the contacts, public holidays.
        services.AddSingleton<IProviderFactory<ICalendarProvider>, Neruna.Providers.Special.BirthdayProviderFactory>();
        services.AddSingleton<IProviderFactory<ICalendarProvider>, Neruna.Providers.Special.HolidayProviderFactory>();
        services.AddSingleton<IProviderFactory<IContactProvider>, Neruna.Providers.Special.LocalAddressBookProviderFactory>();
        services.AddSingleton<AddressCollector>();
        services.AddSingleton<ServerRulesPageViewModel>();
        // Microsoft 365 / Outlook.com: one sign-in for mail, calendar and contacts (see MicrosoftAccount for the app id).
        services.AddSingleton(sp => new Neruna.Providers.Graph.GraphConnectionFactory(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ICredentialStore>()));
        services.AddSingleton<IAccountSignIn, Neruna.Providers.Graph.MicrosoftSignIn>();
        services.AddSingleton<IProviderFactory<IMailProvider>, Neruna.Providers.Graph.GraphMailProviderFactory>();
        services.AddSingleton<IProviderFactory<ICalendarProvider>, Neruna.Providers.Graph.GraphCalendarProviderFactory>();
        services.AddSingleton<IProviderFactory<IContactProvider>, Neruna.Providers.Graph.GraphContactProviderFactory>();
        // Google: one sign-in for Gmail (IMAP/SMTP), the calendar with Google Tasks (CalDAV + REST) and the contacts (CardDAV).
        services.AddSingleton(sp => new Neruna.Providers.Google.GoogleConnectionFactory(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ICredentialStore>()));
        services.AddSingleton<IAccountSignIn, Neruna.Providers.Google.GoogleSignIn>();
        services.AddSingleton<IProviderFactory<IMailProvider>, Neruna.Providers.Google.GoogleMailProviderFactory>();
        services.AddSingleton<IProviderFactory<ICalendarProvider>>(sp => new Neruna.Providers.Google.GoogleCalendarProviderFactory(
            sp.GetRequiredService<Neruna.Providers.Google.GoogleConnectionFactory>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("dav"),
            sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IProviderFactory<IContactProvider>>(sp => new Neruna.Providers.Google.GoogleContactProviderFactory(
            sp.GetRequiredService<Neruna.Providers.Google.GoogleConnectionFactory>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("dav"),
            sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<Neruna.Core.Auth.IBrowserLauncher, Neruna.Desktop.Infrastructure.BrowserLauncher>();
        // Exchange on premises (EWS), also behind a reverse proxy with client certificates.
        services.AddSingleton<Neruna.Core.Security.ClientCertificates>();
        services.AddSingleton<Neruna.Providers.Ews.EwsConnectionFactory>();
        services.AddSingleton<IProviderFactory<IMailProvider>, Neruna.Providers.Ews.EwsMailProviderFactory>();
        services.AddSingleton<IProviderFactory<IMailProvider>, Neruna.Providers.Pst.PstMailProviderFactory>();
        services.AddSingleton<IProviderFactory<ICalendarProvider>, Neruna.Providers.Ews.EwsCalendarProviderFactory>();
        services.AddSingleton<IProviderFactory<IContactProvider>, Neruna.Providers.Ews.EwsContactProviderFactory>();
        // AI providers (Settings → KI): one is connected at a time. Further ones (Claude, Gemini …) are added here.
        services.AddSingleton<Neruna.Core.Ai.IAiProvider>(sp => new Neruna.Providers.OpenAi.OpenAiProvider(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<Neruna.Core.Ai.AiController>();
        // Online meetings (Settings → Besprechungen); Teams in Microsoft 365 calendars comes from Exchange itself.
        services.AddSingleton<Neruna.Core.Meetings.IMeetingProvider>(sp => new Neruna.Providers.Graph.TeamsMeetingProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ICredentialStore>()));
        services.AddSingleton<Neruna.Core.Meetings.IMeetingProvider>(sp => new Neruna.Providers.Zoom.ZoomMeetingProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ICredentialStore>()));
        services.AddSingleton<Neruna.Core.Meetings.IMeetingProvider>(sp => new Neruna.Core.Meetings.JitsiMeetingProvider(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<Neruna.Core.Meetings.MeetingController>();
        if (options.Demo)
        {
            services.AddSingleton<IProviderFactory<IMailProvider>, DemoMailProviderFactory>();
            services.AddSingleton<IProviderFactory<ICalendarProvider>, DemoCalendarProviderFactory>();
            services.AddSingleton<IProviderFactory<IContactProvider>, DemoContactProviderFactory>();
            services.AddSingleton<Neruna.Core.Ai.IAiProvider, DemoAiProvider>();
        }

        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<MailController>();
        services.AddSingleton<MailPushService>();
        services.AddSingleton<Neruna.Core.Calendar.ReminderService>();
        services.AddSingleton<Neruna.Core.Calendar.InvitationService>();
        services.AddSingleton<SignatureService>();
        services.AddSingleton<CalendarController>();
        services.AddSingleton<TaskController>();
        services.AddSingleton<Neruna.Core.SyncCoordinator>();
        services.AddSingleton<Neruna.Core.SyncActivity>();
        services.AddSingleton<ContactController>();
        // Other people's S/MIME certificates go to their contacts (CardDAV, Exchange, Microsoft 365) where there is one.
        services.AddSingleton<Neruna.Core.Security.IContactCertificates>(sp => sp.GetRequiredService<ContactController>());
        services.AddSingleton<AccountSetupService>();
        services.AddSingleton<AccountDiscovery>();
        services.AddSingleton<Neruna.Core.Cloud.CloudController>();
        services.AddSingleton<Neruna.Core.Cloud.CloudSignatureSync>();
        services.AddSingleton<Neruna.Core.Cloud.CloudTextTemplateSync>();
        services.AddSingleton<Neruna.Core.Cloud.CloudMailTagSync>();
        services.AddSingleton<Neruna.Core.Mail.MailTagService>();
        services.AddSingleton<Neruna.Core.Calendar.CalendarCategoryService>();
        services.AddSingleton<Neruna.Core.Cloud.CloudCertificateSync>();
        services.AddSingleton<Neruna.Core.Cloud.CloudPairingService>();
        services.AddSingleton<Neruna.Core.Cloud.AccountTransferService>();
        services.AddSingleton<Neruna.Core.Cloud.SettingsBackupService>();
        services.AddSingleton<Neruna.Core.Cloud.CloudRevocation>();
        services.AddSingleton<Neruna.Core.Cloud.CloudAccountSync>();
        services.AddSingleton<Neruna.Core.Chat.ChatController>();
        services.AddSingleton(_ => CrashHandler.Store);
        services.AddSingleton<Neruna.Core.Diagnostics.CrashReportService>();
        services.AddSingleton<CrashReportsOptionsViewModel>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<UiLayout>();
        services.AddSingleton<UiPreferences>();
        services.AddSingleton<RecipientDirectory>();
        services.AddSingleton<PrivacyService>();
        services.AddSingleton<Neruna.Core.Privacy.PrivacyStatistics>();
        services.AddSingleton<Neruna.Core.Mail.QuickActionSuggestions>();
        services.AddSingleton<AgendaViewModel>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<MailNotifier>();
        services.AddSingleton<AutoRepliesViewModel>();
        services.AddSingleton<ReminderScheduler>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CertificateManager>();
        services.AddSingleton<SecureMimeService>();

        services.AddSingleton<MailViewModel>();
        services.AddSingleton<CalendarViewModel>();
        services.AddSingleton<ContactsViewModel>();
        services.AddSingleton<TasksViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<FileBackupViewModel>();
        services.AddSingleton<CertificatesViewModel>();
        services.AddSingleton<MailOptionsViewModel>();
        services.AddSingleton<PrivacyOptionsViewModel>();
        services.AddSingleton<WorkspacesViewModel>();
        services.AddSingleton<Neruna.Core.News.DevblogFeed>();
        services.AddSingleton<NewsViewModel>();
        services.AddSingleton<CalendarOptionsViewModel>();
        services.AddSingleton<SignaturesViewModel>();
        services.AddSingleton<AppearanceViewModel>();
        services.AddSingleton<CloudViewModel>();
        services.AddSingleton<TextTemplatesViewModel>();
        services.AddSingleton<MailTagsViewModel>();
        services.AddSingleton<CalendarCategoriesViewModel>();
        services.AddSingleton<Neruna.Core.Mail.QuickActionService>();
        services.AddSingleton<Neruna.Core.Mail.PdfProtectionService>();
        services.AddSingleton<PdfOptionsViewModel>();
        services.AddSingleton<QuickActionsViewModel>();
        services.AddSingleton<MeetingsOptionsViewModel>();
        services.AddSingleton(sp => new AiOptionsViewModel(sp.GetRequiredService<Neruna.Core.Ai.AiController>()) { Browser = sp.GetRequiredService<Neruna.Core.Auth.IBrowserLauncher>() });
        services.AddSingleton<TextTemplateService>();
        services.AddSingleton<Neruna.Core.Workspaces.WorkspaceService>();
        services.AddSingleton<MailSearchIndexer>();
        services.AddSingleton<SearchIndexViewModel>();
        services.AddSingleton<LocalDataViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        await provider.InitializeNerunaStorageAsync();

        // Remote wipe when an organisation blocks this device: listens from the start.
        provider.GetRequiredService<Neruna.Core.Cloud.CloudRevocation>();

        // The search index is built in the background from the start.
        provider.GetRequiredService<MailSearchIndexer>().Start();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Neruna");
        logger.LogInformation("Neruna Desktop {Version} started (data: {DataDirectory}, demo: {Demo}, protocol log: {ProtocolLog})",
            typeof(AppServices).Assembly.GetName().Version, options.DataDirectory, options.Demo, options.ProtocolLog);
        // Crashes: logged and kept for a report (CrashHandler, installed in Main).
        CrashHandler.Logger = logger;
        Editor.HtmlEditor.WebViewDataFolder = Path.Combine(options.DataDirectory, "WebView2");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            CrashHandler.RecordUnobserved(e.Exception);
            e.SetObserved();
        };

        if (options.Demo)
        {
            await provider.GetRequiredService<IAccountStore>().SaveAccountAsync(DemoData.CreateAccount());
        }

        return provider;
    }
}
