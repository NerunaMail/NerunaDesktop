using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Discovery;
using Neruna.Providers.Dav;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Konto hinzufügen": email + password → discovery → editable server settings → verify → save.
/// The account is assembled through <see cref="AccountSetupService"/>, so every registered provider
/// takes what it understands from the configuration (IMAP/SMTP, CalDAV, CardDAV).
/// Leaving the IMAP server empty creates a calendar/contacts-only account.
/// </summary>
internal sealed partial class AccountSetupViewModel(AccountDiscovery discovery, AccountSetupService setup, HttpClient davProbeClient) : ViewModelBase
{
    // ── Step 1 for a new account: which kind. Everything with IMAP/CalDAV/CardDAV is one choice (found automatically);
    //    only providers that need their own sign-in are listed separately. ──

    /// <summary>"choose", "imap" or "microsoft".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsImap), nameof(IsMicrosoft), nameof(CanGoBack), nameof(Subtitle))]
    public partial string Step { get; set; } = "choose";

    public bool IsChoosing => Step == "choose";

    public bool IsImap => Step == "imap";

    public bool IsMicrosoft => Step == "microsoft";

    /// <summary>Back to the choice of kind: for a new account after choosing one.</summary>
    public bool CanGoBack => !IsChoosing && !IsEditing;

    /// <summary>Microsoft 365 needs Neruna's app registration; without its client id the choice stays off.</summary>
    public static bool MicrosoftAvailable => Neruna.Providers.Graph.MicrosoftAccount.IsConfigured;

    public static bool MicrosoftUnavailable => !MicrosoftAvailable;

    /// <summary>Set by the shell: the Microsoft sign-in (browser, tokens).</summary>
    public Neruna.Providers.Graph.GraphConnectionFactory? Graph { get; init; }

    public Neruna.Core.Auth.IBrowserLauncher? Browser { get; init; }

    [RelayCommand]
    private void ChooseImap() => Step = "imap";

    [RelayCommand]
    private void ChooseMicrosoft()
    {
        if (MicrosoftAvailable)
        {
            Step = "microsoft";
        }
    }

    [RelayCommand]
    private void Back()
    {
        Error = null;
        Step = "choose";
    }

    /// <summary>
    /// Sign-in in the browser; then the account (mail, calendar, contacts) is set up with the name and address
    /// Microsoft reports. Editing: sign in again (e.g. after the password changed) and keep the account.
    /// </summary>
    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        if (Graph is null || Browser is null)
        {
            return;
        }

        Error = null;
        IsBusy = true;
        BusyText = T("Anmeldung im Browser …");
        try
        {
            var tokenId = Editing?.ConnectionsOf(ServiceKind.Mail).FirstOrDefault()?.Id ?? Guid.NewGuid();
            await Graph.SignInAsync(tokenId, string.IsNullOrWhiteSpace(Email) ? null : Email.Trim(), Browser);
            if (Editing is { } existing)
            {
                await setup.RenameAsync(existing, string.IsNullOrWhiteSpace(DisplayName) ? existing.DisplayName : DisplayName.Trim(), Label);
                Finished?.Invoke(this, true);
                return;
            }

            BusyText = T("Konto wird eingerichtet …");
            var (email, name) = await Graph.WhoAmIAsync(tokenId);
            var account = new Account(Guid.NewGuid(), string.IsNullOrWhiteSpace(DisplayName) ? name : DisplayName.Trim(), email,
                Neruna.Providers.Graph.GraphConnectionFactory.Connections(email, tokenId), string.IsNullOrWhiteSpace(Label) ? null : Label.Trim());
            await setup.CreateSignedInAsync(account);
            CreatedAccount = account;
            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever goes wrong (browser, keychain, Graph): a message in the dialog, never a crash.
            Error = ex is Neruna.Core.Auth.OAuthException or AccountSetupException or Neruna.Providers.Graph.GraphException
                ? ex.Message
                : F("Anmeldung fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>Editing a Microsoft account: only the names (servers belong to Microsoft).</summary>
    [RelayCommand]
    private async Task SaveMicrosoftAsync()
    {
        if (Editing is { } existing)
        {
            await setup.RenameAsync(existing, string.IsNullOrWhiteSpace(DisplayName) ? existing.DisplayName : DisplayName.Trim(), Label);
            Finished?.Invoke(this, true);
        }
    }

    /// <summary>Raised with true when an account was created, false when cancelled.</summary>
    public event EventHandler<bool>? Finished;

    /// <summary>The account just set up (not when editing): the shell shows it at once and syncs it first.</summary>
    public Account? CreatedAccount { get; private set; }

    public static IReadOnlyList<SocketSecurity> SecurityOptions { get; } = [SocketSecurity.SslOnConnect, SocketSecurity.StartTls, SocketSecurity.None];

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional name of the account in Neruna (folder tree, lists) instead of the e-mail address.</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>Further sender addresses of this mailbox (the mail server has to allow them).</summary>
    public System.Collections.ObjectModel.ObservableCollection<AliasRow> Aliases { get; } = [];

    [RelayCommand]
    private void AddAlias() => Aliases.Add(new AliasRow { DisplayName = DisplayName });

    [RelayCommand]
    private void RemoveAlias(AliasRow row) => Aliases.Remove(row);

    /// <summary>"Konto bearbeiten": the account being changed (its servers prefilled; an empty password keeps the old one).</summary>
    public Account? Editing { get; private set; }

    public bool IsEditing => Editing is not null;

    /// <summary>Set up by the organisation: servers, sender and aliases are read-only here.</summary>
    public bool IsCloud => Editing?.IsFromCloud == true;

    public string Title => IsEditing ? T("Konto bearbeiten") : T("Konto hinzufügen");

    public string SaveText => IsEditing ? T("Speichern") : T("Konto hinzufügen");

    public string Subtitle => IsChoosing && !IsEditing
        ? T("Welche Art von Konto möchten Sie einrichten?")
        : IsMicrosoft
            ? T("Mail, Kalender und Kontakte Ihres Microsoft-Kontos – angemeldet wird im Browser.")
            : IsCloud
        ? T("Die Verbindungen werden vor dem Speichern geprüft; Mails, Termine und Kontakte bleiben erhalten.")
        : IsEditing
        ? T("Namen, Passwort und Server ändern. Die Verbindungen werden vor dem Speichern geprüft; Mails, Termine und Kontakte bleiben erhalten. Leeres Kalender- oder Kontaktfeld entfernt diesen Dienst.")
        : T("E-Mail (IMAP/SMTP), Kalender (CalDAV) und Kontakte (CardDAV) – z. B. SOGo, Nextcloud, Mailcow oder Ihr Provider. Die Servereinstellungen werden automatisch gesucht.");

    public string PasswordHint => IsEditing ? T("leer lassen = unverändert") : string.Empty;

    /// <summary>Shows an existing account for editing: names and the servers its connections describe.</summary>
    public void LoadForEditing(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Editing = account;
        Step = account.Connections.Any(c => c.ProviderId == Neruna.Core.Providers.ProviderIds.Graph) ? "microsoft" : "imap";
        DisplayName = account.DisplayName;
        Label = account.Label ?? string.Empty;
        Aliases.Clear();
        foreach (var alias in account.Aliases ?? [])
        {
            Aliases.Add(new AliasRow { Email = alias.Email, DisplayName = alias.DisplayName });
        }

        Email = account.EmailAddress ?? string.Empty;
        var config = setup.Describe(account);
        ImapHost = SmtpHost = string.Empty;
        if (config.IncomingServers.FirstOrDefault() is { } imap)
        {
            ImapHost = imap.Host;
            ImapPort = imap.Port.ToString(CultureInfo.InvariantCulture);
            ImapSecurity = imap.Security;
            Username = imap.UsernameTemplate;
        }

        if (config.OutgoingServers.FirstOrDefault() is { } smtp)
        {
            SmtpHost = smtp.Host;
            SmtpPort = smtp.Port.ToString(CultureInfo.InvariantCulture);
            SmtpSecurity = smtp.Security;
        }

        CalDavUrl = config.CalDav?.ServerUrl.AbsoluteUri ?? string.Empty;
        CardDavUrl = config.CardDav?.ServerUrl.AbsoluteUri ?? string.Empty;
        if (Username.Length == 0)
        {
            Username = config.DavServers.FirstOrDefault()?.UsernameTemplate ?? string.Empty;
        }

        ShowServerSettings = true;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsCloud));
        OnPropertyChanged(nameof(PasswordHint));
        CreateCommand.NotifyCanExecuteChanged();
    }

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
        BusyText = T("Suche Servereinstellungen …");
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
                Error = T("Keine automatische Konfiguration gefunden. Bitte Serverdaten prüfen.");
            }
            else
            {
                Apply(result);
            }

            // Many SOGo/Nextcloud setups publish DAV only via /.well-known, not in autoconfig.
            BusyText = T("Suche Kalender- und Kontaktdienst …");
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
            Error = T("Bitte eine gültige E-Mail-Adresse eingeben.");
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
            Error = T("Bitte eine gültige E-Mail-Adresse eingeben.");
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
                Error = T("Ports müssen Zahlen sein.");
                return;
            }

            incoming.Add(new MailServerSettings(ServerProtocol.Imap, ImapHost.Trim(), imapPort, ImapSecurity, AuthScheme.PasswordCleartext, username));
            outgoing.Add(new MailServerSettings(ServerProtocol.Smtp, SmtpHost.Trim(), smtpPort, SmtpSecurity, AuthScheme.PasswordCleartext, username));
        }

        var dav = new List<DavServerSettings>();
        if (!TryAddDav(dav, ServerProtocol.CalDav, CalDavUrl, username) || !TryAddDav(dav, ServerProtocol.CardDav, CardDavUrl, username))
        {
            Error = T("Die CalDAV-/CardDAV-Adresse muss mit https:// oder http:// beginnen.");
            return;
        }

        var name = string.IsNullOrWhiteSpace(DisplayName) ? email : DisplayName.Trim();
        var config = new MailProviderConfig(domain, null, incoming, outgoing, dav);
        var label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim();
        var aliases = new List<MailIdentity>();
        foreach (var row in Aliases.Where(r => !string.IsNullOrWhiteSpace(r.Email)))
        {
            var address = row.Email.Trim();
            if (!MimeKit.MailboxAddress.TryParse(address, out var parsed) || parsed.Address != address || !address.Contains('@', StringComparison.Ordinal))
            {
                Error = F("«{0}» ist keine gültige E-Mail-Adresse.", address);
                return;
            }

            if (!string.Equals(address, email, StringComparison.OrdinalIgnoreCase) && aliases.All(a => !string.Equals(a.Email, address, StringComparison.OrdinalIgnoreCase)))
            {
                aliases.Add(new MailIdentity(address, string.IsNullOrWhiteSpace(row.DisplayName) ? name : row.DisplayName.Trim()));
            }
        }

        var account = setup.BuildAccount(name, email, config) with { Label = label, Aliases = aliases.Count > 0 ? aliases : null };
        if (account.Connections.Count == 0)
        {
            Error = T("Bitte mindestens einen IMAP-Server oder eine CalDAV-/CardDAV-Adresse angeben.");
            return;
        }

        IsBusy = true;
        BusyText = T("Verbindungen werden geprüft …");
        try
        {
            if (Editing is { } existing)
            {
                await setup.UpdateAsync(existing with { Aliases = account.Aliases }, name, label, email, config, Password.Length > 0 ? Password : null);
            }
            else
            {
                await setup.CreateAsync(account, Password);
                CreatedAccount = account;
            }

            Finished?.Invoke(this, true);
        }
        catch (AccountSetupException ex)
        {
            Error = T("Verbindung fehlgeschlagen – ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanCreate() => !IsBusy && (IsEditing || Password.Length > 0);

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
            ? F("Konfiguration von {0} (Neruna) übernommen", org)
            : F("Konfiguration gefunden über {0}", result.Source);

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

internal sealed partial class AliasRow : ObservableObject
{
    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;
}
