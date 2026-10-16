using CommunityToolkit.Mvvm.ComponentModel;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>"Einstellungen": accounts with diagnostics, mail options, signatures and S/MIME certificates.</summary>
internal sealed partial class SettingsViewModel(AccountsViewModel accounts, MailOptionsViewModel mailOptions, CalendarOptionsViewModel calendarOptions, SignaturesViewModel signatures, CertificatesViewModel certificates, AppearanceViewModel appearance, Neruna.Core.Security.ICredentialStore credentials, Neruna.Desktop.Infrastructure.UpdateService updates, CloudViewModel cloud, TextTemplatesViewModel textTemplates, CrashReportsOptionsViewModel crashReports, AiOptionsViewModel ai, FileBackupViewModel fileBackup, QuickActionsViewModel quickActions, SearchIndexViewModel searchIndex, LocalDataViewModel localData, PrivacyOptionsViewModel privacy, WorkspacesViewModel workspaces) : ViewModelBase
{
    /// <summary>Settings → Workspaces.</summary>
    public WorkspacesViewModel Workspaces => workspaces;

    /// <summary>Index of the «Workspaces» tab (the switcher's "Workspaces verwalten").</summary>
    public const int WorkspacesTab = 15;

    /// <summary>Opens Einstellungen → Workspaces.</summary>
    public void ShowWorkspaces()
    {
        SelectedTab = WorkspacesTab;
        _ = Workspaces.ReloadAsync();
    }

    /// <summary>Settings → Datenschutz.</summary>
    public PrivacyOptionsViewModel Privacy => privacy;

    /// <summary>The devblog on neruna.org in the app's language (Über Neruna).</summary>
    public static Uri DevblogUri => new($"https://neruna.org/{Neruna.Core.Localization.Texts.Language}/blog/");

    /// <summary>Settings → Daten → Lokale Daten.</summary>
    public LocalDataViewModel LocalData => localData;

    /// <summary>Settings → E-Mail → Suchindex.</summary>
    public SearchIndexViewModel SearchIndex => searchIndex;

    /// <summary>Settings → E-Mail → Schnellaktionen (and the PDF stamps).</summary>
    public QuickActionsViewModel QuickActions => quickActions;

    /// <summary>Settings → Datensicherung: the personal settings into a password-protected file and back.</summary>
    public FileBackupViewModel FileBackup => fileBackup;

    /// <summary>Settings → KI: AI provider and model.</summary>
    public AiOptionsViewModel Ai => ai;

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

    /// <summary>Index of the «Cloud» tab (the chat sends people there to connect).</summary>
    public const int CloudTab = 7;

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>
    /// The list on the left: areas, the mail ones under a heading. <see cref="SettingsNavItem.Tab"/> keeps the old tab
    /// numbers, so jumps into the settings (e.g. to <see cref="CloudTab"/>) keep working.
    /// </summary>
    public static IReadOnlyList<SettingsNavItem> Navigation { get; } =
    [
        new(-1, T("Konten"), null, IsHeading: true),
        new(0, T("Konten"), "IconPeople"),
        new(7, T("Neruna Cloud"), "IconCloud"),
        new(-1, T("E-Mail"), null, IsHeading: true),
        new(1, T("Allgemein"), "IconMail"),
        new(3, T("Signaturen"), "IconSignature"),
        new(4, T("Textvorlagen"), "IconTextTemplate"),
        new(11, T("Schnellaktionen"), "IconFlash"),
        new(5, T("Zertifikate (S/MIME)"), "IconCertificate"),
        new(14, T("Datenschutz"), "IconShield"),
        new(-1, T("Kalender"), null, IsHeading: true),
        new(2, T("Kalender"), "IconCalendar"),
        new(-1, T("Daten"), null, IsHeading: true),
        new(12, T("Suchindex"), "IconSearch"),
        new(10, T("Datensicherung"), "IconArchive"),
        new(13, T("Lokale Daten"), "IconFolder"),
        new(-1, T("Programm"), null, IsHeading: true),
        new(6, T("Darstellung"), "IconPalette"),
        new(15, T("Workspaces"), "IconFolder"),
        new(8, T("KI"), "IconAi"),
        new(9, T("Über Neruna"), "IconInfo"),
    ];

    [ObservableProperty]
    public partial SettingsNavItem? SelectedNav { get; set; } = Navigation[1];

    partial void OnSelectedTabChanged(int value)
    {
        if (SelectedNav?.Tab != value)
        {
            SelectedNav = Navigation.FirstOrDefault(n => n.Tab == value);
        }
    }

    partial void OnSelectedNavChanged(SettingsNavItem? value)
    {
        if (value is { IsHeading: false } item)
        {
            SelectedTab = item.Tab;
            if (item.Tab == 12)
            {
                _ = SearchIndex.ReloadAsync();
            }
            else if (item.Tab == 13)
            {
                LocalData.ShowDatabaseSize();
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

/// <summary>An entry of the settings navigation (or a heading over a group).</summary>
internal sealed record SettingsNavItem(int Tab, string Title, string? IconKey, bool IsHeading = false, bool IsIndented = false)
{
    public Avalonia.Thickness Indent => IsIndented ? new Avalonia.Thickness(14, 0, 0, 0) : default;
}
