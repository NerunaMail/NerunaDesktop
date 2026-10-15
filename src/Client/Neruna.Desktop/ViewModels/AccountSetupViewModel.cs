using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Discovery;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;
using Neruna.Providers.Dav;
using Neruna.Providers.Ews;
using Neruna.Providers.Imap;
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
    /// <summary>Set by the shell: TLS client certificates (Exchange behind a reverse proxy); without it none can be chosen.</summary>
    public ClientCertificates? Certificates { get; init; }

    /// <summary>Set by the shell: picks a certificate file.</summary>
    public IFileService? Files { get; init; }

    // ── Step 1 for a new account: which kind. Everything with IMAP/CalDAV/CardDAV is one choice (found automatically);
    //    only providers that need their own sign-in are listed separately. ──

    /// <summary>"choose", "imap", "exchange", "proton", "nextcloud" or "microsoft".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsImap), nameof(IsExchange), nameof(IsProton), nameof(IsNextcloud), nameof(IsMailForm), nameof(IsMicrosoft), nameof(CanGoBack),
        nameof(Subtitle), nameof(NeedsPassword), nameof(PasswordLabel))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Step { get; set; } = "choose";

    public bool IsChoosing => Step == "choose";

    public bool IsImap => Step == "imap";

    /// <summary>Exchange on premises through EWS (also behind a reverse proxy with client certificate).</summary>
    public bool IsExchange => Step == "exchange";

    /// <summary>Calendars and contacts of a Nextcloud, signed in in the browser (app password, two-factor sign-in works).</summary>
    public bool IsNextcloud => Step == "nextcloud";

    /// <summary>Set by the shell: a web client that follows redirects (Nextcloud's sign-in).</summary>
    public HttpClient? Web { get; init; }

    /// <summary>"cloud.example.com" or the full address of the Nextcloud.</summary>
    [ObservableProperty]
    public partial string NextcloudServer { get; set; } = string.Empty;

    [RelayCommand]
    private void ChooseNextcloud() => Step = "nextcloud";

    /// <summary>
    /// Nextcloud Login Flow v2: the browser opens, the user signs in and allows access; Nextcloud gives Neruna an app
    /// password. Then calendars and contacts are set up as an account of their own (the mail stays where it is).
    /// </summary>
    [RelayCommand]
    private async Task SignInNextcloudAsync()
    {
        if (Browser is null || Web is null)
        {
            return;
        }

        Error = null;
        IsBusy = true;
        BusyText = T("Anmeldung im Browser … (bitte dort den Zugriff erlauben)");
        try
        {
            var login = await NextcloudLoginFlow.SignInAsync(Web, NextcloudServer, Browser);
            BusyText = T("Verbindungen werden geprüft …");
            var email = Email.Trim();
            var config = new MailProviderConfig(login.Server.Host, null, [], [],
                [new DavServerSettings(ServerProtocol.CalDav, login.DavUrl, login.LoginName), new DavServerSettings(ServerProtocol.CardDav, login.DavUrl, login.LoginName)]);
            var name = string.IsNullOrWhiteSpace(DisplayName) ? login.LoginName : DisplayName.Trim();
            var account = setup.BuildAccount(name, email, config) with
            {
                EmailAddress = email.Contains('@', StringComparison.Ordinal) ? email : null,
                Label = string.IsNullOrWhiteSpace(Label) ? "Nextcloud" : Label.Trim(),
            };
            await setup.CreateAsync(account, login.AppPassword);
            CreatedAccount = account;
            Finished?.Invoke(this, true);
        }
        catch (NextcloudLoginException ex)
        {
            Error = ex.Message;
        }
        catch (AccountSetupException ex)
        {
            Error = T("Verbindung fehlgeschlagen – ") + ex.Message;
        }
        catch (Neruna.Core.Auth.OAuthException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>Proton Mail through Proton Mail Bridge on this computer (IMAP/SMTP on 127.0.0.1).</summary>
    public bool IsProton => Step == "proton";

    /// <summary>Name, address, aliases and password: the same for IMAP, Exchange and Proton.</summary>
    public bool IsMailForm => IsImap || IsExchange || IsProton;

    public string PasswordLabel => IsProton ? T("Passwort aus der Bridge") : T("Passwort");

    public bool IsMicrosoft => Step == "microsoft";

    /// <summary>Back to the choice of kind: for a new account after choosing one.</summary>
    public bool CanGoBack => !IsChoosing && !IsEditing;

    /// <summary>Microsoft 365 needs Neruna's app registration; without its client id the choice stays off.</summary>
    public bool MicrosoftAvailable => setup.CanSignIn(Neruna.Core.Providers.ProviderIds.Graph);

    /// <summary>Set by the shell: opens the sign-in page in the system browser.</summary>
    public Neruna.Core.Auth.IBrowserLauncher? Browser { get; init; }

    [RelayCommand]
    private void ChooseImap() => Step = "imap";

    [RelayCommand]
    private async Task ChooseProtonAsync()
    {
        Step = "proton";
        ShowServerSettings = true;

        // The Bridge's own defaults until the search finds others.
        ImapPort = ProtonBridge.DefaultImapPort.ToString(CultureInfo.InvariantCulture);
        SmtpPort = ProtonBridge.DefaultSmtpPort.ToString(CultureInfo.InvariantCulture);
        ImapSecurity = SmtpSecurity = SocketSecurity.StartTls;
        await DetectBridgeAsync();
    }

    [RelayCommand]
    private async Task ChooseExchangeAsync()
    {
        Step = "exchange";
        ShowServerSettings = true;
        await LoadCertificatesAsync(null);
    }

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
        ShowServerSettings = false;
        Step = "choose";
    }

    /// <summary>
    /// Sign-in in the browser; then the account (mail, calendar, contacts) is set up with the name and address
    /// Microsoft reports. Editing: sign in again (e.g. after the password changed) and keep the account.
    /// </summary>
    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        if (Browser is null)
        {
            return;
        }

        Error = null;
        IsBusy = true;
        BusyText = T("Anmeldung im Browser …");
        var loginHint = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        try
        {
            if (Editing is { } existing)
            {
                await setup.SignInAgainAsync(existing, string.IsNullOrWhiteSpace(DisplayName) ? existing.DisplayName : DisplayName.Trim(), Label, loginHint, Browser);
            }
            else
            {
                CreatedAccount = await setup.CreateBySignInAsync(Neruna.Core.Providers.ProviderIds.Graph, DisplayName, Label, loginHint, Browser);
            }

            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever goes wrong (browser, keychain, the provider): a message in the dialog, never a crash.
            Error = ex is Neruna.Core.Auth.OAuthException or AccountSetupException
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

    /// <summary>
    /// "Jedes Zertifikat akzeptieren": for test servers with a self-signed certificate. Not offered for accounts from
    /// Neruna Cloud – their servers must have proper certificates.
    /// </summary>
    [ObservableProperty]
    public partial bool AcceptAnyCertificate { get; set; }

    public string Title => IsEditing ? T("Konto bearbeiten") : T("Konto hinzufügen");

    public string SaveText => IsEditing ? T("Speichern") : T("Konto hinzufügen");

    public string Subtitle => IsChoosing && !IsEditing
        ? T("Welche Art von Konto möchten Sie einrichten?")
        : IsMicrosoft
            ? T("Mail, Kalender und Kontakte Ihres Microsoft-Kontos – angemeldet wird im Browser.")
            : IsNextcloud
            ? T("Kalender und Kontakte Ihrer Nextcloud – angemeldet wird im Browser, auch mit Zwei-Faktor-Anmeldung. Die E-Mail richten Sie als eigenes Konto ein.")
            : IsProton
            ? T("Proton Mail über die Proton Mail Bridge auf diesem Computer. Die Bridge braucht ein kostenpflichtiges Proton-Abo; Kalender und Kontakte bietet sie nicht an.")
            : IsExchange && !IsCloud
            ? IsEditing
                ? T("Namen, Passwort, Server und Zertifikat ändern. Die Verbindung wird vor dem Speichern geprüft; Mails, Termine und Kontakte bleiben erhalten.")
                : T("E-Mail, Kalender und Kontakte eines Exchange-Servers (2013 oder neuer) über Exchange Web Services – auch hinter einem Reverse Proxy mit Client-Zertifikat.")
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
        var ews = account.Connections.FirstOrDefault(c => c.ProviderId == Neruna.Core.Providers.ProviderIds.Ews);
        var bridge = account.Connections.FirstOrDefault(c => c.Settings.GetValueOrDefault(ImapSettings.KindKey) == ImapSettings.ProtonBridgeKind);
        Step = setup.IsSignedIn(account) ? "microsoft" : ews is not null ? "exchange" : bridge is not null ? "proton" : "imap";
        _bridgeFingerprints = bridge?.Settings.GetValueOrDefault(TlsCertificates.PinnedCertificateKey)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        DisplayName = account.DisplayName;
        Label = account.Label ?? string.Empty;
        Aliases.Clear();
        foreach (var alias in account.Aliases ?? [])
        {
            Aliases.Add(new AliasRow { Email = alias.Email, DisplayName = alias.DisplayName });
        }

        Email = account.EmailAddress ?? string.Empty;
        _linkSettings = account.Connections.Select(c => c.Settings).FirstOrDefault(s => s.Keys.Any(k => k.StartsWith("provider.", StringComparison.Ordinal)))
            ?.Where(p => p.Key.StartsWith("provider.", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value) ?? [];
        ProviderLinkItems.Clear();
        foreach (var link in ProviderLinks.Of(account))
        {
            ProviderLinkItems.Add(link);
        }

        HasProviderLinks = ProviderLinkItems.Count > 0;

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
        AcceptAnyCertificate = account.Connections.Any(c => Neruna.Core.Security.TlsCertificates.AcceptsAnyCertificate(c.Settings));
        if (ews is not null)
        {
            var settings = EwsConnectionFactory.SettingsOf(ews);
            ExchangeServer = settings.Url.AbsoluteUri;
            Username = settings.Username;
            ExchangeAuthentication = AuthenticationChoices.First(c => c.Value == settings.Authentication);
            _ = LoadCertificatesAsync(TlsCertificates.ClientCertificateOf(ews.Settings));
        }

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

    /// <summary>Webmail, instructions and support of the provider, from its configuration (kept with the account).</summary>
    public System.Collections.ObjectModel.ObservableCollection<LinkItem> ProviderLinkItems { get; } = [];

    [ObservableProperty]
    public partial bool HasProviderLinks { get; set; }

    /// <summary>What the provider says to switch on first (e.g. IMAP in the webmail settings); null if nothing.</summary>
    [ObservableProperty]
    public partial LinkItem? EnableHint { get; set; }

    private Dictionary<string, string> _linkSettings = [];

    private void ShowLinks(MailProviderConfig config)
    {
        _linkSettings = new Dictionary<string, string>(ProviderLinks.ToSettings(config));
        ProviderLinkItems.Clear();
        foreach (var link in ProviderLinks.Of(config))
        {
            ProviderLinkItems.Add(link);
        }

        EnableHint = config.Enable is { } enable ? ProviderLinks.Item(enable) : null;
        HasProviderLinks = ProviderLinkItems.Count > 0;
    }

    [RelayCommand]
    private async Task OpenLinkAsync(LinkItem link)
    {
        if (Browser is not null)
        {
            try
            {
                await Browser.OpenAsync(link.Url);
            }
            catch (Neruna.Core.Auth.OAuthException)
            {
                Error = link.Url.AbsoluteUri; // no browser: at least the address to copy
            }
        }
    }

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
            var caldav = CalDavUrl.Length == 0 ? await DavProbe.FindAsync(davProbeClient, hosts, "caldav", domain: domain) : null;
            var carddav = CardDavUrl.Length == 0 ? await DavProbe.FindAsync(davProbeClient, hosts, "carddav", domain: domain) : null;
            if (await AllowedAsync(caldav, carddav) is { } allowed)
            {
                CalDavUrl = allowed.Contains(caldav) ? caldav!.Url.AbsoluteUri : CalDavUrl;
                CardDavUrl = allowed.Contains(carddav) ? carddav!.Url.AbsoluteUri : CardDavUrl;
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

    /// <summary>
    /// The found services that may be used. One that a server redirected to another host would get the password there:
    /// the user decides, once for both (unless it is the mail server itself, which has the password anyway).
    /// </summary>
    private async Task<HashSet<DavProbeResult?>?> AllowedAsync(DavProbeResult? caldav, DavProbeResult? carddav)
    {
        var found = new HashSet<DavProbeResult?> { caldav, carddav };
        found.Remove(null);
        var elsewhere = found.Where(f => f!.IsElsewhere && !f.Url.Host.Equals(ImapHost.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (elsewhere.Count == 0)
        {
            return found;
        }

        var targets = string.Join("\n", elsewhere.Select(f => f!.Url.AbsoluteUri).Distinct(StringComparer.Ordinal));
        var allow = await (AskRedirect?.Invoke(T("Weiterleitung erlauben?"),
            F("{0} leitet Kalender bzw. Kontakte an einen anderen Server weiter:\n\n{1}\n\nNeruna meldet sich dort mit Ihrem Benutzernamen und Passwort an. Erlauben Sie das nur, wenn Sie diesen Server kennen.", elsewhere[0]!.ProbedHost, targets))
            ?? Task.FromResult(false));
        if (!allow)
        {
            found.ExceptWith(elsewhere);
        }

        return found;
    }

    /// <summary>Set by the shell: asks a yes/no question (title, message); without it, redirects elsewhere are not used.</summary>
    public Func<string, string, Task<bool>>? AskRedirect { get; init; }

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
        var dav = new List<DavServerSettings>();
        var options = new Dictionary<string, string>();
        if (IsExchange)
        {
            // Left empty: the address from the domain's configuration or Neruna's directory, if it has one.
            var url = EwsSettings.ParseServer(ExchangeServer);
            if (url is null && string.IsNullOrWhiteSpace(ExchangeServer))
            {
                IsBusy = true;
                BusyText = T("Suche Servereinstellungen …");
                try
                {
                    var discovered = await discovery.DiscoverAsync(email);
                    if (discovered is not null)
                    {
                        ShowLinks(discovered.Config);
                    }

                    if (discovered?.Config.Ews is { } found)
                    {
                        url = found.ServerUrl;
                        ExchangeServer = url.AbsoluteUri;
                        if (Username.Trim().Length == 0)
                        {
                            username = DiscoveryPlaceholders.Expand(found.UsernameTemplate, email);
                            Username = username;
                        }
                    }
                }
                finally
                {
                    IsBusy = false;
                    BusyText = null;
                }
            }

            if (url is null)
            {
                Error = T("Bitte den Exchange-Server angeben, z. B. mail.example.com.");
                return;
            }

            dav.Add(new DavServerSettings(ServerProtocol.Ews, url, username));
            if (ExchangeAuthentication.Value != EwsAuthentication.Automatic)
            {
                options[EwsSettings.AuthenticationKey] = ExchangeAuthentication.Value.ToString();
            }

            if (SelectedCertificate?.Value is { } certificate)
            {
                options[TlsCertificates.ClientCertificateKey] = certificate;
            }
        }
        else if (ImapHost.Trim().Length > 0)
        {
            if (IsProton)
            {
                ImapHost = SmtpHost = ProtonBridge.Host;
                if (_bridgeFingerprints.Count == 0 && int.TryParse(ImapPort, NumberStyles.None, CultureInfo.InvariantCulture, out var bridgeImap)
                    && int.TryParse(SmtpPort, NumberStyles.None, CultureInfo.InvariantCulture, out var bridgeSmtp))
                {
                    _bridgeFingerprints = await ProtonBridge.FingerprintsAsync(bridgeImap, ImapSecurity, bridgeSmtp, SmtpSecurity);
                }

                foreach (var (key, value) in ProtonBridge.ConnectionOptions(_bridgeFingerprints))
                {
                    options[key] = value;
                }
            }

            if (!int.TryParse(ImapPort, NumberStyles.None, CultureInfo.InvariantCulture, out var imapPort)
                || !int.TryParse(SmtpPort, NumberStyles.None, CultureInfo.InvariantCulture, out var smtpPort))
            {
                Error = T("Ports müssen Zahlen sein.");
                return;
            }

            incoming.Add(new MailServerSettings(ServerProtocol.Imap, ImapHost.Trim(), imapPort, ImapSecurity, AuthScheme.PasswordCleartext, username));
            outgoing.Add(new MailServerSettings(ServerProtocol.Smtp, SmtpHost.Trim(), smtpPort, SmtpSecurity, AuthScheme.PasswordCleartext, username));
        }

        if (!IsExchange && (!TryAddDav(dav, ServerProtocol.CalDav, CalDavUrl, username) || !TryAddDav(dav, ServerProtocol.CardDav, CardDavUrl, username)))
        {
            Error = T("Die CalDAV-/CardDAV-Adresse muss mit https:// oder http:// beginnen.");
            return;
        }

        // Exchange sends with the mailbox's primary address and the name from its directory: no name, no aliases here.
        var name = IsExchange ? (Editing?.DisplayName ?? email) : string.IsNullOrWhiteSpace(DisplayName) ? email : DisplayName.Trim();
        var config = new MailProviderConfig(domain, null, incoming, outgoing, dav);
        var label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim();
        var aliases = new List<MailIdentity>();
        foreach (var row in Aliases.Where(r => !IsExchange && !string.IsNullOrWhiteSpace(r.Email)))
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

        foreach (var (key, value) in _linkSettings)
        {
            options.TryAdd(key, value);
        }

        var account = setup.BuildAccount(name, email, config, AcceptAnyCertificate && !IsCloud, options) with { Label = label, Aliases = aliases.Count > 0 ? aliases : null };
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
                await setup.UpdateAsync(existing with { Aliases = account.Aliases }, name, label, email, config, Password.Length > 0 && NeedsPassword ? Password : null,
                    acceptAnyCertificate: AcceptAnyCertificate && !IsCloud, options: options);
            }
            else
            {
                await setup.CreateAsync(account, NeedsPassword ? Password : null);
                CreatedAccount = account;
            }

            Finished?.Invoke(this, true);
        }
        catch (AccountSetupException ex)
        {
            Error = T("Verbindung fehlgeschlagen – ") + ex.Message;

            // Calendar or contacts refused the password while mail may take it: often an app password is needed.
            if (ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                Error += " " + T("Ist es eine Nextcloud mit Zwei-Faktor-Anmeldung? Dann braucht es für Kalender und Kontakte ein App-Passwort – einfacher: CalDAV/CardDAV hier leer lassen und Kalender und Kontakte als Kontoart «Nextcloud» einrichten (Anmeldung im Browser).");
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanCreate() => !IsBusy && (IsEditing || Password.Length > 0 || !NeedsPassword);

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    // ── Proton Mail Bridge ──

    private IReadOnlyList<string> _bridgeFingerprints = [];

    /// <summary>What the search found (or not), shown in the Proton step.</summary>
    [ObservableProperty]
    public partial string? BridgeStatus { get; set; }

    [ObservableProperty]
    public partial bool BridgeFound { get; set; }

    /// <summary>
    /// Looks for the Bridge: its usual ports first, then every port open on this computer. Fills in ports and
    /// encryption and remembers the Bridge's certificate.
    /// </summary>
    [RelayCommand]
    private async Task DetectBridgeAsync()
    {
        Error = null;
        IsBusy = true;
        BusyText = T("Suche Proton Mail Bridge …");
        try
        {
            var found = await ProtonBridge.DetectAsync();
            ImapHost = SmtpHost = ProtonBridge.Host;
            if (found.Imap is { } imap)
            {
                ImapPort = imap.Port.ToString(CultureInfo.InvariantCulture);
                ImapSecurity = imap.Security;
            }

            if (found.Smtp is { } smtp)
            {
                SmtpPort = smtp.Port.ToString(CultureInfo.InvariantCulture);
                SmtpSecurity = smtp.Security;
            }

            _bridgeFingerprints = [.. new[] { found.Imap?.CertificateFingerprint, found.Smtp?.CertificateFingerprint }.OfType<string>().Distinct()];
            BridgeFound = found.IsComplete;
            BridgeStatus = found.IsComplete
                ? F("Bridge gefunden: IMAP Port {0}, SMTP Port {1}.", found.Imap!.Port, found.Smtp!.Port)
                : found.Imap is not null
                    ? found.SmtpCandidates.Count > 0
                        ? F("Bridge gefunden (IMAP Port {0}), SMTP nicht eindeutig – in Frage kommen die Ports {1}. Bitte den SMTP-Port aus der Bridge eintragen.", found.Imap.Port, string.Join(", ", found.SmtpCandidates))
                        : F("Bridge gefunden (IMAP Port {0}), aber kein SMTP. Bitte den SMTP-Port aus der Bridge eintragen.", found.Imap.Port)
                    : T("Keine Bridge gefunden. Läuft die Proton Mail Bridge, und ist Ihr Konto dort angemeldet? Danach erneut suchen – oder die Ports aus der Bridge unten eintragen.");
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    // ── Exchange (EWS) ──

    /// <summary>Here, not in XAML: a backslash there would be read as an escape.</summary>
    public static string ExchangeUserHint => T("anna@example.com oder FIRMA\\anna – leer = E-Mail-Adresse");

    /// <summary>"mail.example.com" or the full EWS address.</summary>
    [ObservableProperty]
    public partial string ExchangeServer { get; set; } = string.Empty;

    public static IReadOnlyList<Choice<EwsAuthentication>> AuthenticationChoices { get; } =
    [
        new(EwsAuthentication.Automatic, T("Automatisch (Kerberos/NTLM oder Basic)")),
        new(EwsAuthentication.Basic, T("Basic (für Reverse Proxys)")),
        new(EwsAuthentication.CertificateOnly, T("Nur Client-Zertifikat, ohne Passwort")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPassword))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial Choice<EwsAuthentication> ExchangeAuthentication { get; set; } = AuthenticationChoices[0];

    /// <summary>False for Exchange with the client certificate alone.</summary>
    public bool NeedsPassword => !IsExchange || ExchangeAuthentication.Value != EwsAuthentication.CertificateOnly;

    /// <summary>"Kein Client-Zertifikat", the system's certificates (Windows, macOS) and the one the account uses or was imported.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Choice<string?>> CertificateChoices { get; } = [];

    [ObservableProperty]
    public partial Choice<string?>? SelectedCertificate { get; set; }

    [ObservableProperty]
    public partial bool IsCertificatePasswordVisible { get; set; }

    [ObservableProperty]
    public partial string CertificatePassword { get; set; } = string.Empty;

    private byte[]? _pendingCertificate;

    private async Task LoadCertificatesAsync(string? current)
    {
        CertificateChoices.Clear();
        CertificateChoices.Add(new Choice<string?>(null, T("Kein Client-Zertifikat")));
        foreach (var system in ClientCertificates.SystemCertificates())
        {
            CertificateChoices.Add(new Choice<string?>(system.Reference, system.DisplayName));
        }

        if (current is not null && CertificateChoices.All(c => c.Value != current))
        {
            var info = Certificates is null ? null : await Certificates.DescribeAsync(current);
            CertificateChoices.Add(new Choice<string?>(current, info?.DisplayName ?? T("Zertifikat fehlt auf diesem Gerät – bitte neu wählen")));
        }

        SelectedCertificate = CertificateChoices.FirstOrDefault(c => c.Value == current) ?? CertificateChoices[0];
    }

    /// <summary>A .p12/.pfx file: kept in the keychain (with its password) and chosen for this account.</summary>
    [RelayCommand]
    private async Task ImportCertificateAsync()
    {
        if (Files is null || Certificates is null || (await Files.PickFilesAsync(T("Client-Zertifikat wählen (.p12, .pfx)"))).FirstOrDefault() is not { } path)
        {
            return;
        }

        try
        {
            _pendingCertificate = await File.ReadAllBytesAsync(path);
        }
        catch (IOException ex)
        {
            Error = F("«{0}» konnte nicht gelesen werden: {1}", Path.GetFileName(path), ex.Message);
            return;
        }

        CertificatePassword = string.Empty;
        await ImportPendingAsync(string.Empty);
    }

    [RelayCommand]
    private Task ConfirmCertificateAsync() => ImportPendingAsync(CertificatePassword);

    [RelayCommand]
    private void CancelCertificate()
    {
        _pendingCertificate = null;
        IsCertificatePasswordVisible = false;
    }

    private async Task ImportPendingAsync(string password)
    {
        if (_pendingCertificate is not { } data || Certificates is null)
        {
            return;
        }

        Error = null;
        try
        {
            var info = await Certificates.ImportAsync(data, password);
            _pendingCertificate = null;
            IsCertificatePasswordVisible = false;
            var choice = CertificateChoices.FirstOrDefault(c => c.Value == info.Reference) ?? new Choice<string?>(info.Reference, info.DisplayName);
            if (!CertificateChoices.Contains(choice))
            {
                CertificateChoices.Add(choice);
            }

            SelectedCertificate = choice;
        }
        catch (CertificatePasswordException) when (password.Length == 0)
        {
            IsCertificatePasswordVisible = true; // protected by a password: ask for it
        }
        catch (Exception ex) when (ex is CertificatePasswordException or FormatException)
        {
            Error = ex.Message;
        }
    }

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
        ShowLinks(result.Config);
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
