using Neruna.Core.Auth;

namespace Neruna.Core.Accounts;

/// <summary>Who signed in, and the connections (mail, calendar, contacts) that one sign-in serves.</summary>
public sealed record SignedInAccount(string EmailAddress, string DisplayName, IReadOnlyList<ServiceConnection> Connections);

/// <summary>
/// An account set up by signing in in the browser (OAuth) instead of with servers and a password – today Microsoft 365
/// and Outlook.com, later others. The provider owns everything protocol-specific; account setup and the UI only see this.
/// </summary>
public interface IAccountSignIn
{
    /// <summary>The provider id of the connections it creates (e.g. <see cref="Providers.ProviderIds.Graph"/>).</summary>
    string ProviderId { get; }

    /// <summary>False when this build has no app registration for it (the choice stays off).</summary>
    bool IsAvailable { get; }

    /// <param name="existing">Signing in again (password changed, new permissions): its tokens are replaced, and the
    /// result has the account's connections as they are to be now – services allowed since added (new ids), withdrawn
    /// ones left out, the others unchanged.</param>
    /// <exception cref="AccountSetupException">The sign-in failed or was cancelled.</exception>
    Task<SignedInAccount> SignInAsync(Account? existing, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default);

    /// <summary>
    /// Services of <paramref name="account"/> the user did not allow at the provider (names for the user, e.g.
    /// "Kalender") – signing in again with them allowed adds them.
    /// </summary>
    Task<IReadOnlyList<string>> NotGrantedAsync(Account account, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
