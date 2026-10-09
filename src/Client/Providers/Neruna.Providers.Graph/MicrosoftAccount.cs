using Neruna.Core.Auth;

namespace Neruna.Providers.Graph;

/// <summary>
/// Neruna's app registration in Microsoft Entra (multi-tenant, personal accounts too; public client, no secret).
/// The client id is not secret (see <see cref="OAuthEndpoints"/>). Forks register their own app and put its id here.
/// While it is empty, "Microsoft 365" stays switched off in the account dialog.
/// </summary>
public static class MicrosoftAccount
{
    /// <summary>Application (client) ID from portal.azure.com → Microsoft Entra ID → App registrations.</summary>
    public const string ClientId = "d365c924-b087-40b6-88a7-823a382785fc";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public static OAuthEndpoints Endpoints { get; } = new(
        new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
        new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
        ClientId,
        ["offline_access", "openid", "email", "User.Read", "Mail.ReadWrite", "Mail.Send", "MailboxSettings.Read", "Calendars.ReadWrite", "Contacts.ReadWrite"]);
}

/// <summary>What a Graph connection stores (no secrets: the tokens are in the keychain under <see cref="TokenId"/>).</summary>
public sealed record GraphSettings(string User, Guid TokenId)
{
    public const string UserKey = "graph.user";
    public const string TokenKey = "graph.token";

    public static GraphSettings FromDictionary(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GraphSettings(settings.GetValueOrDefault(UserKey) ?? string.Empty,
            Guid.TryParse(settings.GetValueOrDefault(TokenKey), out var id) ? id : Guid.Empty);
    }

    public IReadOnlyDictionary<string, string> ToDictionary() =>
        new Dictionary<string, string> { [UserKey] = User, [TokenKey] = TokenId.ToString("D") };
}
