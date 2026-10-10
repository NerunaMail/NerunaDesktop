using Neruna.Core.Accounts;
using Neruna.Core.Auth;
using Neruna.Core.Providers;

namespace Neruna.Providers.Graph;

/// <summary>Microsoft 365 / Outlook.com: sign-in in the browser, then mail, calendar and contacts with one token.</summary>
public sealed class MicrosoftSignIn(GraphConnectionFactory graph) : IAccountSignIn
{
    public string ProviderId => ProviderIds.Graph;

    public bool IsAvailable => MicrosoftAccount.IsConfigured;

    public async Task<SignedInAccount> SignInAsync(Account? existing, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        // Signing in again keeps the token id, so the account's connections stay as they are.
        var tokenId = existing?.Connections.Where(c => c.ProviderId == ProviderIds.Graph)
                          .Select(c => (Guid?)GraphSettings.FromDictionary(c.Settings).TokenId).FirstOrDefault()
                      ?? Guid.NewGuid();
        try
        {
            await graph.SignInAsync(tokenId, loginHint, browser, cancellationToken);
            var (email, name) = await graph.WhoAmIAsync(tokenId, cancellationToken);
            return new SignedInAccount(email, name, existing?.Connections ?? GraphConnectionFactory.Connections(email, tokenId));
        }
        catch (GraphException ex)
        {
            throw new AccountSetupException(ex.Message, ex);
        }
    }
}
