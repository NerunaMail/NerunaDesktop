using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Discovery;
using Neruna.Providers.Dav;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Konto hinzufügen": email + password → discovery → editable server settings → verify → save.
/// The account is assembled through <see cref="AccountSetupService"/>, so every registered provider
/// takes what it understands from the configuration (IMAP/SMTP, CalDAV, CardDAV).
/// Leaving the IMAP server empty creates a calendar/contacts-only account.
/// </summary>
internal sealed partial class AccountSetupViewModel(AccountDiscovery discovery, AccountSetupService setup, HttpClient davProbeClient) : ViewModelBase
{
    /// <summary>Raised with true when an account was created, false when cancelled.</summary>
    public event EventHandler<bool>? Finished;

    public static IReadOnlyList<SocketSecurity> SecurityOptions { get; } = [SocketSecurity.SslOnConnect, SocketSecurity.StartTls, SocketSecurity.None];

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCommand))]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowServerSettings { get; set; }

    [ObservableProperty]
    public partial string? DiscoverySource { get; set; }

    [ObservableProperty]
    public partial string ImapHost { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImapPort { get; set; } = "993";

    [ObservableProperty]
    public partial SocketSecurity ImapSecurity { get; set; } = SocketSecurity.SslOnConnect;

    [ObservableProperty]
    public partial string SmtpHost { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SmtpPort { get; set; } = "587";

    [ObservableProperty]
    public partial SocketSecurity SmtpSecurity { get; set; } = SocketSecurity.StartTls;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CalDavUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CardDavUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCommand), nameof(CreateCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverAsync()
    {
        Error = null;
        IsBusy = true;
        BusyText = "Suche Servereinstellungen …";
        try
        {
            var email = Email.Trim();
            var (_, domain) = DiscoveryPlaceholders.SplitAddress(email);
            var result = await discovery.DiscoverAsync(email);
            if (result is null)
            {
                DiscoverySource = null;
                ImapHost = SmtpHost = "mail." + domain;
                Username = email;
                Error = "Keine automatische Konfiguration gefunden. Bitte Serverdaten prüfen.";
            }
            else
            {
                Apply(result);
            }

            // Many SOGo/Nextcloud setups publish DAV only via /.well-known, not in autoconfig.
            BusyText = "Suche Kalender- und Kontaktdienst …";
            string[] hosts = [domain, ImapHost.Trim()];
            if (CalDavUrl.Length == 0 && await DavProbe.FindAsync(davProbeClient, hosts, "caldav") is { } caldav)
            {
                CalDavUrl = caldav.AbsoluteUri;
            }

            if (CardDavUrl.Length == 0 && await DavProbe.FindAsync(davProbeClient, hosts, "carddav") is { } carddav)
            {
                CardDavUrl = carddav.AbsoluteUri;
            }

            ShowServerSettings = true;
        }
        catch (FormatException)
        {
            Error = "Bitte eine gültige E-Mail-Adresse eingeben.";
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanDiscover() => !IsBusy && Email.Contains('@', StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Error = null;
        var email = Email.Trim();
        string domain;
        try
        {
            (_, domain) = DiscoveryPlaceholders.SplitAddress(email);
        }
        catch (FormatException)
        {
            Error = "Bitte eine gültige E-Mail-Adresse eingeben.";
            return;
        }

        var username = Username.Trim().Length > 0 ? Username.Trim() : email;
        var incoming = new List<MailServerSettings>();
        var outgoing = new List<MailServerSettings>();
        if (ImapHost.Trim().Length > 0)
        {
            if (!int.TryParse(ImapPort, NumberStyles.None, CultureInfo.InvariantCulture, out var imapPort)
                || !int.TryParse(SmtpPort, NumberStyles.None, CultureInfo.InvariantCulture, out var smtpPort))
            {
                Error = "Ports müssen Zahlen sein.";
                return;
            }

            incoming.Add(new MailServerSettings(ServerProtocol.Imap, ImapHost.Trim(), imapPort, ImapSecurity, AuthScheme.PasswordCleartext, username));
            outgoing.Add(new MailServerSettings(ServerProtocol.Smtp, SmtpHost.Trim(), smtpPort, SmtpSecurity, AuthScheme.PasswordCleartext, username));
        }

        var dav = new List<DavServerSettings>();
        if (!TryAddDav(dav, ServerProtocol.CalDav, CalDavUrl, username) || !TryAddDav(dav, ServerProtocol.CardDav, CardDavUrl, username))
        {
            Error = "Die CalDAV-/CardDAV-Adresse muss mit https:// oder http:// beginnen.";
            return;
        }

        var name = string.IsNullOrWhiteSpace(DisplayName) ? email : DisplayName.Trim();
        var account = setup.BuildAccount(name, email, new MailProviderConfig(domain, null, incoming, outgoing, dav));
        if (account.Connections.Count == 0)
        {
            Error = "Bitte mindestens einen IMAP-Server oder eine CalDAV-/CardDAV-Adresse angeben.";
            return;
        }

        IsBusy = true;
        BusyText = "Verbindungen werden geprüft …";
        try
        {
            await setup.CreateAsync(account, Password);
            Finished?.Invoke(this, true);
        }
        catch (AccountSetupException ex)
        {
            Error = "Verbindung fehlgeschlagen – " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanCreate() => !IsBusy && Password.Length > 0;

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private static bool TryAddDav(List<DavServerSettings> target, ServerProtocol protocol, string url, string username)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return false;
        }

        target.Add(new DavServerSettings(protocol, uri, username));
        return true;
    }

    private void Apply(DiscoveryResult result)
    {
        DiscoverySource = result.OrganizationName is { } org
            ? $"Konfiguration von {org} (Neruna) übernommen"
            : $"Konfiguration gefunden über {result.Source}";

        var email = Email.Trim();
        if (result.Config.IncomingServers.FirstOrDefault(s => s.Protocol == ServerProtocol.Imap) is { } imap)
        {
            ImapHost = imap.Host;
            ImapPort = imap.Port.ToString(CultureInfo.InvariantCulture);
            ImapSecurity = imap.Security;
            Username = DiscoveryPlaceholders.Expand(imap.UsernameTemplate, email);
        }

        if (result.Config.PreferredOutgoing is { } smtp)
        {
            SmtpHost = smtp.Host;
            SmtpPort = smtp.Port.ToString(CultureInfo.InvariantCulture);
            SmtpSecurity = smtp.Security;
        }

        CalDavUrl = result.Config.CalDav?.ServerUrl.AbsoluteUri ?? string.Empty;
        CardDavUrl = result.Config.CardDav?.ServerUrl.AbsoluteUri ?? string.Empty;
    }
}
