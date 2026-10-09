using CommunityToolkit.Mvvm.ComponentModel;

namespace Neruna.Desktop.ViewModels;

/// <summary>"Einstellungen": accounts with diagnostics, mail options, signatures and S/MIME certificates.</summary>
internal sealed partial class SettingsViewModel(AccountsViewModel accounts, MailOptionsViewModel mailOptions, CalendarOptionsViewModel calendarOptions, SignaturesViewModel signatures, CertificatesViewModel certificates, AppearanceViewModel appearance, Neruna.Core.Security.ICredentialStore credentials, Neruna.Desktop.Infrastructure.UpdateService updates, CloudViewModel cloud, TextTemplatesViewModel textTemplates) : ViewModelBase
{
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
        await CalendarOptions.ReloadAsync();
        await Signatures.ReloadAsync();
        await Certificates.ReloadAsync();
        await Appearance.ReloadAsync();
        await TextTemplates.ReloadAsync();
        await Cloud.ReloadAsync();
    }
}
