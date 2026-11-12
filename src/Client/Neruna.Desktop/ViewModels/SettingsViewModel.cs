using CommunityToolkit.Mvvm.ComponentModel;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>"Einstellungen": accounts with diagnostics, mail options, signatures and S/MIME certificates.</summary>
internal sealed partial class SettingsViewModel(AccountsViewModel accounts, MailOptionsViewModel mailOptions, CalendarOptionsViewModel calendarOptions, SignaturesViewModel signatures, CertificatesViewModel certificates, AppearanceViewModel appearance, Neruna.Core.Security.ICredentialStore credentials, Neruna.Desktop.Infrastructure.UpdateService updates, CloudViewModel cloud, TextTemplatesViewModel textTemplates, CrashReportsOptionsViewModel crashReports, AiOptionsViewModel ai, FileBackupViewModel fileBackup, QuickActionsViewModel quickActions, SearchIndexViewModel searchIndex, LocalDataViewModel localData, PrivacyOptionsViewModel privacy, WorkspacesViewModel workspaces, ProfilesViewModel profiles, NewsViewModel news, MeetingsOptionsViewModel meetings, PdfOptionsViewModel pdf, ServerRulesPageViewModel serverRules, MailTagsViewModel mailTags, DeleteConfirmationsViewModel deleteConfirmations, Neruna.Core.ISettingsStore settingsStore) : ViewModelBase
{
    /// <summary>Settings → Schlagwörter.</summary>
    public MailTagsViewModel MailTags => mailTags;


    /// <summary>Opens Einstellungen → Schlagwörter ("Verwalten" in a mail's tag menu).</summary>
    public async Task ShowMailTagsAsync()
    {
        Section = SettingsSection.MailTags;
        await MailTags.ReloadAsync();
    }

    /// <summary>Settings → Serverregeln: the rules of every mail account in one place.</summary>
    public ServerRulesPageViewModel ServerRules => serverRules;


    /// <summary>Opens Einstellungen → Serverregeln on this account.</summary>
    public async Task ShowServerRulesAsync(Guid accountId)
    {
        _ruleAccount = accountId;
        Section = SettingsSection.ServerRules;
        if (_ruleAccount is { } id)
        {
            _ruleAccount = null;
            await ServerRules.ShowAsync(id);
        }
    }

    private Guid? _ruleAccount;

    /// <summary>Settings → Programm → News: the devblog.</summary>
    public NewsViewModel News => news;


    /// <summary>Settings → Workspaces.</summary>
    public WorkspacesViewModel Workspaces => workspaces;

    /// <summary>Settings → Profile.</summary>
    public ProfilesViewModel Profiles => profiles;





    /// <summary>Opens Einstellungen → Workspaces.</summary>
    public void ShowWorkspaces()
    {
        Section = SettingsSection.Workspaces;
        Profiles.Reload();
        _ = Workspaces.ReloadAsync();
    }

    /// <summary>Settings → Datenschutz.</summary>
    public PrivacyOptionsViewModel Privacy => privacy;

    /// <summary>The devblog on neruna.org in the app's language (Über Neruna).</summary>
    public static Uri DevblogUri => new($"https://neruna.org/{Neruna.Core.Localization.Texts.Language}/blog/");

    /// <summary>Settings → Daten → Lokale Daten.</summary>
    public LocalDataViewModel LocalData => localData;

    /// <summary>"Vor dem Löschen fragen" (Daten).</summary>
    public DeleteConfirmationsViewModel DeleteConfirmations => deleteConfirmations;

    /// <summary>Settings → E-Mail → Suchindex.</summary>
    public SearchIndexViewModel SearchIndex => searchIndex;

    /// <summary>Settings → E-Mail → Schnellaktionen (and the PDF stamps).</summary>
    public QuickActionsViewModel QuickActions => quickActions;

    /// <summary>Settings → Datensicherung: the personal settings into a password-protected file and back.</summary>
    public FileBackupViewModel FileBackup => fileBackup;

    /// <summary>Settings → KI: AI provider and model.</summary>
    public AiOptionsViewModel Ai => ai;

    /// <summary>Settings → Besprechungen: Teams, Zoom, Jitsi.</summary>
    public MeetingsOptionsViewModel Meetings => meetings;


    /// <summary>Settings → PDF: stamps and passwords for PDF attachments.</summary>
    public PdfOptionsViewModel Pdf => pdf;


    /// <summary>Settings → Info: crash reports.</summary>
    public CrashReportsOptionsViewModel CrashReports => crashReports;

    /// <summary>Settings → Textvorlagen.</summary>
    public TextTemplatesViewModel TextTemplates => textTemplates;

    /// <summary>Settings → Cloud: connection with the organisation's Neruna server.</summary>
    public CloudViewModel Cloud => cloud;

    /// <summary>Settings → Info: version and updates.</summary>
    public Neruna.Desktop.Infrastructure.UpdateService Updates => updates;

    /// <summary>Settings → Info: where account and certificate passwords are kept.</summary>
    public string CredentialLocation => credentials.Location;

    public AccountsViewModel Accounts { get; } = accounts;

    public MailOptionsViewModel MailOptions { get; } = mailOptions;

    public CalendarOptionsViewModel CalendarOptions { get; } = calendarOptions;

    public SignaturesViewModel Signatures { get; } = signatures;

    public CertificatesViewModel Certificates { get; } = certificates;

    public AppearanceViewModel Appearance { get; } = appearance;



    /// <summary>The page shown; opening the settings shows the one used last (the first time «Konten»).</summary>
    [ObservableProperty]
    public partial SettingsSection Section { get; set; } = SettingsSection.Accounts;

    /// <summary>
    /// The list on the left: the pages most people need first (accounts, mail, calendar), the rarely used ones
    /// («Erweitert», «Über Neruna») at the end. Headings group them.
    /// </summary>
    public static IReadOnlyList<SettingsNavItem> Navigation { get; } =
    [
        new(SettingsSection.Accounts, T("Konten"), "IconPeople"),
        new(SettingsSection.Cloud, T("Neruna Cloud"), "IconCloud"),
        SettingsNavItem.Heading(T("E-Mail")),
        new(SettingsSection.Mail, T("Allgemein"), "IconMail"),
        new(SettingsSection.Compose, T("Schreiben"), "IconEdit"),
        new(SettingsSection.Signatures, T("Signaturen"), "IconSignature"),
        new(SettingsSection.TextTemplates, T("Textvorlagen"), "IconTextTemplate"),
        new(SettingsSection.MailTags, T("Schlagwörter"), "IconTag"),
        new(SettingsSection.QuickActions, T("Schnellaktionen"), "IconFlash"),
        new(SettingsSection.ServerRules, T("Serverregeln"), "IconInbox"),
        SettingsNavItem.Heading(T("Kalender")),
        new(SettingsSection.Calendar, T("Kalender"), "IconCalendar"),
        new(SettingsSection.Meetings, T("Besprechungen"), "IconVideo"),
        SettingsNavItem.Gap,
        new(SettingsSection.Appearance, T("Darstellung"), "IconPalette"),
        SettingsNavItem.Heading(T("Sicherheit")),
        new(SettingsSection.Privacy, T("Datenschutz"), "IconShield"),
        new(SettingsSection.Certificates, T("Zertifikate (S/MIME)"), "IconCertificate"),
        new(SettingsSection.Pdf, "PDF", "IconLock"),
        SettingsNavItem.Gap,
        new(SettingsSection.Data, T("Daten"), "IconArchive"),
        SettingsNavItem.Heading(T("Erweitert")),
        new(SettingsSection.Workspaces, T("Profile und Workspaces"), "IconWorkspaces"),
        new(SettingsSection.Ai, T("KI"), "IconAi"),
        .. MailOptionsViewModel.CanBeDefaultApp || MailOptionsViewModel.CanOfferMapi ? [new SettingsNavItem(SettingsSection.Integration, T("Integration"), "IconLink")] : Array.Empty<SettingsNavItem>(),
        SettingsNavItem.Heading(T("Über Neruna")),
        new(SettingsSection.About, T("Über Neruna"), "IconInfo"),
        new(SettingsSection.News, T("News"), "IconRss"),
    ];

    /// <summary>An account has to be signed in again: "Konten" in the list carries a "!".</summary>
    [ObservableProperty]
    public partial bool AccountsNeedSignIn { get; set; }

    [ObservableProperty]
    public partial SettingsNavItem? SelectedNav { get; set; } = Navigation[0];

    private bool _restoring;

    partial void OnSectionChanged(SettingsSection value)
    {
        if (SelectedNav?.Section != value || SelectedNav.IsHeading)
        {
            SelectedNav = Navigation.FirstOrDefault(n => !n.IsHeading && n.Section == value);
        }

        if (!_restoring)
        {
            _ = settingsStore.SetAsync(Neruna.Core.SettingKeys.SettingsPage, value.ToString());
        }
    }

    /// <summary>The page used last (saved with each change), shown when Neruna starts.</summary>
    public async Task RestoreSectionAsync()
    {
        if (Enum.TryParse<SettingsSection>(await settingsStore.GetAsync(Neruna.Core.SettingKeys.SettingsPage), out var last)
            && Navigation.Any(n => !n.IsHeading && n.Section == last))
        {
            _restoring = true;
            try
            {
                Section = last;
            }
            finally
            {
                _restoring = false;
            }
        }
    }

    partial void OnSelectedNavChanged(SettingsNavItem? value)
    {
        if (value is { IsHeading: false } item)
        {
            Section = item.Section;
            switch (item.Section)
            {
                case SettingsSection.Data:
                    _ = SearchIndex.ReloadAsync();
                    LocalData.ShowDatabaseSize();
                    _ = DeleteConfirmations.LoadAsync();
                    break;
                case SettingsSection.ServerRules when _ruleAccount is null:
                    _ = ServerRules.LoadAsync();
                    break;
                case SettingsSection.MailTags:
                    _ = MailTags.ReloadAsync();
                    break;
                case SettingsSection.Pdf:
                    _ = Pdf.LoadAsync();
                    break;
                case SettingsSection.Meetings:
                    _ = Meetings.LoadAsync();
                    break;
                case SettingsSection.News:
                    _ = News.ShowAsync();
                    break;
                case SettingsSection.Workspaces:
                    Profiles.Reload();
                    _ = Workspaces.ReloadAsync();
                    break;
            }
        }
    }

    /// <summary>Central signatures arrived or went: the list here follows.</summary>
    public void Attach(Neruna.Core.Cloud.CloudSignatureSync cloudSignatures, Neruna.Core.Cloud.CloudTextTemplateSync cloudTemplates, Neruna.Core.Cloud.CloudCertificateSync cloudCertificates)
    {
        ArgumentNullException.ThrowIfNull(cloudCertificates);
        cloudCertificates.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Certificates.ReloadAsync());
        ArgumentNullException.ThrowIfNull(cloudSignatures);
        ArgumentNullException.ThrowIfNull(cloudTemplates);
        cloudSignatures.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Signatures.ReloadAsync());
        cloudTemplates.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await TextTemplates.ReloadAsync());
    }

    public async Task ReloadAsync()
    {
        await RestoreSectionAsync();
        await Accounts.ReloadAsync();
        await MailOptions.ReloadAsync();
        await Privacy.ReloadAsync();
        await CalendarOptions.ReloadAsync();
        await Signatures.ReloadAsync();
        await Certificates.ReloadAsync();
        await Appearance.ReloadAsync();
        await TextTemplates.ReloadAsync();
        await QuickActions.ReloadAsync();
        await Cloud.ReloadAsync();
        await Workspaces.ReloadAsync();
    }
}

/// <summary>The pages of the settings, by name (jumps from elsewhere use these, not positions in the list).</summary>
internal enum SettingsSection
{
    Accounts,
    Cloud,
    Mail,
    Compose,
    Signatures,
    TextTemplates,
    MailTags,
    QuickActions,
    ServerRules,
    Calendar,
    Meetings,
    Appearance,
    Privacy,
    Certificates,
    Pdf,
    Data,
    Workspaces,
    Ai,
    Integration,
    About,
    News,
}

/// <summary>An entry of the settings navigation (or a heading over a group).</summary>
internal sealed record SettingsNavItem(SettingsSection Section, string Title, string? IconKey, bool IsHeading = false)
{
    public static SettingsNavItem Heading(string title) => new(default, title, null, IsHeading: true);

    /// <summary>Space between groups of one page each (a heading would only repeat the page's name).</summary>
    public static SettingsNavItem Gap => new(default, string.Empty, null, IsHeading: true);

    /// <summary>A heading with text (a gap has none and stays low).</summary>
    public bool IsTitled => IsHeading && Title.Length > 0;

    /// <summary>The accounts page (where a "!" can show an account to sign in again).</summary>
    public bool IsAccounts => !IsHeading && Section == SettingsSection.Accounts;
}
