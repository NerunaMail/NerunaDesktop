using System.Reflection;
using Neruna.Core.Auth;

namespace Neruna.Providers.Google;

/// <summary>
/// Neruna's app registration at Google (Google Cloud Console → APIs &amp; Services → Clients, type "Desktop app"). The
/// client id is not secret (see <see cref="OAuthEndpoints"/>); forks register their own app and put its id here.
/// While id or secret are missing, "Google" stays switched off in the account dialog.
/// </summary>
public static class GoogleAccount
{
    public const string ClientId = "425936390758-90e9f87nlj7og1koboa42fi16jlkvlk0.apps.googleusercontent.com";

    /// <summary>
    /// Google insists on the desktop client's secret on the token request (despite PKCE) but treats it as public for
    /// installed apps. It comes with the build (property or environment variable <c>GoogleClientSecret</c>) so it is not
    /// in the public repository.
    /// </summary>
    public static string ClientSecret { get; } =
        typeof(GoogleAccount).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GoogleClientSecret")?.Value ?? string.Empty;

    /// <summary>
    /// The app is in "Testing" at Google until it is verified: only addresses entered as testers can sign in (and their
    /// sign-in lasts 7 days). The account dialog tells how to become a tester; false once Google has verified the app.
    /// </summary>
    public const bool InTesting = true;

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Gmail over IMAP and SMTP (XOAUTH2) – Google allows no narrower scope for that.</summary>
    public const string MailScope = "https://mail.google.com/";

    /// <summary>Google Calendar over CalDAV.</summary>
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar";

    /// <summary>Contacts over CardDAV (Google's CardDAV takes only this scope).</summary>
    public const string ContactsScope = "https://www.googleapis.com/auth/carddav";

    /// <summary>Google Tasks (REST; Google's CalDAV has no tasks).</summary>
    public const string TasksScope = "https://www.googleapis.com/auth/tasks";

    public static OAuthEndpoints Endpoints { get; } = new(
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        ClientId,
        ["openid", "email", "profile", MailScope, CalendarScope, ContactsScope, TasksScope])
    {
        // Google requires the IP address for a desktop app's loopback redirect.
        RedirectHost = "127.0.0.1",
        // A refresh token comes only with access_type=offline, and on a repeated sign-in only after asking again.
        SelectAccount = false,
        AuthorizeParameters = new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "select_account consent" },
        Issuer = "https://accounts.google.com",
        // Google keeps the granted scopes with the refresh token.
        ScopeOnTokenRequests = false,
        ClientSecret = ClientSecret,
    };

    /// <summary>Who signed in (address and name), with the access token.</summary>
    public static readonly Uri UserInfo = new("https://openidconnect.googleapis.com/v1/userinfo");

    public const string ImapHost = "imap.gmail.com";

    public const string SmtpHost = "smtp.gmail.com";

    /// <summary>The CalDAV principal of the address (Google's documented entry point).</summary>
    public static Uri CalDav(string email) => new($"https://apidata.googleusercontent.com/caldav/v2/{Uri.EscapeDataString(email)}/user");

    /// <summary>CardDAV: Google's well-known address leads to the signed-in user's address book.</summary>
    public static readonly Uri CardDav = new("https://www.googleapis.com/.well-known/carddav");
}

/// <summary>What a Google connection stores (no secrets: the tokens are in the keychain under <see cref="TokenId"/>).</summary>
public sealed record GoogleSettings(string User, Guid TokenId)
{
    public const string UserKey = "google.user";
    public const string TokenKey = "google.token";

    public static GoogleSettings FromDictionary(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GoogleSettings(settings.GetValueOrDefault(UserKey) ?? string.Empty,
            Guid.TryParse(settings.GetValueOrDefault(TokenKey), out var id) ? id : Guid.Empty);
    }

    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        [UserKey] = User,
        [TokenKey] = TokenId.ToString("D"),
    };
}
