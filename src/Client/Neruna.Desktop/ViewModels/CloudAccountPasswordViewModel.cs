using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using Neruna.Core.Discovery;
using Neruna.Providers.Dav;
using Neruna.Providers.Imap;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// An account the organisation set up that needs the user once: its password (IMAP, Exchange, CalDAV/CardDAV), the
/// password from Proton Mail Bridge, or a sign-in in the browser (Microsoft 365, Google, Nextcloud). The organisation
/// decided the rest (names, reply address, signatures). "Später" asks again on the next start.
/// </summary>
internal sealed partial class CloudAccountPasswordViewModel(PendingCloudAccount pending, CloudAccountSync sync) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    /// <summary>For the sign-in kinds (set by the shell).</summary>
    public AccountSetupService? Setup { get; init; }

    public Neruna.Core.Auth.IBrowserLauncher? Browser { get; init; }

    public HttpClient? Web { get; init; }

    /// <summary>The account once set up (the shell turns on Nextcloud Talk for a Nextcloud account).</summary>
    public PendingCloudAccount Pending => pending;

    public bool SignsIn => pending.SignsIn;

    public bool AsksPassword => !pending.SignsIn;

    public bool IsProton => pending.Kind == CloudAccountKinds.Proton;

    public string Intro => pending.Kind switch
    {
        CloudAccountKinds.Microsoft365 or CloudAccountKinds.Google =>
            F("Ihre Organisation hat das Konto «{0} <{1}>» für Sie eingerichtet. Melden Sie sich im Browser mit dieser Adresse an, um die Einrichtung abzuschliessen.", pending.DisplayName, pending.Email),
        CloudAccountKinds.Nextcloud =>
            F("Ihre Organisation hat die Nextcloud «{0}» für Sie eingerichtet. Melden Sie sich im Browser an, um die Einrichtung abzuschliessen.", pending.Server ?? pending.Email),
        CloudAccountKinds.Proton =>
            F("Ihre Organisation hat das Konto «{0} <{1}>» für Sie eingerichtet. Starten Sie die Proton Mail Bridge, melden Sie sich dort an und geben Sie hier das Passwort ein, das die Bridge für dieses Konto anzeigt.", pending.DisplayName, pending.Email),
        _ => F("Ihre Organisation hat das Konto «{0} <{1}>» für Sie eingerichtet. Geben Sie das Passwort ein, um die Einrichtung abzuschliessen – es wird nur auf diesem Gerät gespeichert.", pending.DisplayName, pending.Email),
    };

    public string PasswordLabel => IsProton ? T("Bridge-Passwort") : T("Passwort");

    public string SignInText => pending.Kind switch
    {
        CloudAccountKinds.Microsoft365 => T("Bei Microsoft anmelden"),
        CloudAccountKinds.Google => T("Bei Google anmelden"),
        _ => T("Bei Nextcloud anmelden"),
    };

    public string Email => pending.Email;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CompleteAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            await sync.CompleteAsync(IsProton ? await WithBridgeAsync() : pending, Password);
            Finished?.Invoke(this, true);
        }
        catch (AccountSetupException ex)
        {
            Error = T("Anmeldung fehlgeschlagen – ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanComplete() => !IsBusy && Password.Length > 0;

    // Proton: the Bridge's actual ports and certificates on this computer.
    private async Task<PendingCloudAccount> WithBridgeAsync()
    {
        var found = await ProtonBridge.DetectAsync();
        if (found.Imap is not { } imap || found.Smtp is not { } smtp)
        {
            throw new AccountSetupException(T("Keine Bridge gefunden. Läuft die Proton Mail Bridge, und ist Ihr Konto dort angemeldet?"));
        }

        var account = pending.Account;
        var options = ProtonBridge.ConnectionOptions([imap.CertificateFingerprint, smtp.CertificateFingerprint]);
        var connections = account.Connections.Select(c => c.Kind != ServiceKind.Mail || c.ProviderId != Neruna.Core.Providers.ProviderIds.Imap ? c : c with
        {
            Settings = Bridged(c.Settings, options, imap, smtp),
        }).ToList();
        return pending with { Account = account with { Connections = connections } };
    }

    private static Dictionary<string, string> Bridged(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options, BridgeEndpoint imap, BridgeEndpoint smtp)
    {
        var current = ImapSettings.FromDictionary(settings);
        var updated = new Dictionary<string, string>(settings);
        foreach (var (key, value) in options.Concat((current with { ImapPort = imap.Port, ImapSecurity = imap.Security, SmtpPort = smtp.Port, SmtpSecurity = smtp.Security }).ToDictionary()))
        {
            updated[key] = value;
        }

        return updated;
    }

    // Microsoft 365, Google, Nextcloud: signed in here, then the organisation's names and signatures apply.
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        if (Setup is null || Browser is null)
        {
            return;
        }

        Error = null;
        IsBusy = true;
        try
        {
            Account account;
            if (pending.Kind == CloudAccountKinds.Nextcloud)
            {
                if (Web is null)
                {
                    return;
                }

                var login = await NextcloudLoginFlow.SignInAsync(Web, pending.Server ?? string.Empty, Browser);
                var config = new MailProviderConfig(login.Server.Host, null, [], [],
                    [new DavServerSettings(ServerProtocol.CalDav, login.DavUrl, login.LoginName), new DavServerSettings(ServerProtocol.CardDav, login.DavUrl, login.LoginName)]);
                account = Setup.BuildAccount(pending.DisplayName, pending.Email, config) with { Label = "Nextcloud" };
                await Setup.CreateAsync(account, login.AppPassword);
            }
            else
            {
                account = await Setup.CreateBySignInAsync(pending.Kind == CloudAccountKinds.Google ? Neruna.Core.Providers.ProviderIds.Google : Neruna.Core.Providers.ProviderIds.Graph,
                    pending.DisplayName, null, pending.Email, Browser);
            }

            await sync.AdoptAsync(pending, account);
            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever goes wrong (browser, keychain, the provider): a message in the dialog, never a crash.
            Error = ex is Neruna.Core.Auth.OAuthException or AccountSetupException or NextcloudLoginException
                ? ex.Message
                : F("Anmeldung fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSignIn() => !IsBusy && Setup is not null && Browser is not null;

    [RelayCommand]
    private void Later() => Finished?.Invoke(this, false);
}
