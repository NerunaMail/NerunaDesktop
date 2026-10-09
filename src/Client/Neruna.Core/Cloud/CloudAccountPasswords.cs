using System.Security.Cryptography;
using System.Text;
using Neruna.Core.Accounts;
using Neruna.Core.Security;

namespace Neruna.Core.Cloud;

/// <summary>
/// The password a user typed in for an account of the organisation that came without one. Kept per cloud account (not
/// only per connection), so it survives the account being set up again and goes into the personal backup.
/// </summary>
public static class CloudAccountPasswords
{
    public static Guid SecretId(string cloudId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("neruna-cloud-account:" + cloudId)).AsSpan(0, 16));

    /// <summary>Remembers it, and gives it to the account's connections if the account is already here.</summary>
    public static async Task SetAsync(ICredentialStore credentials, IAccountStore accounts, string cloudId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(accounts);
        await credentials.SetSecretAsync(SecretId(cloudId), password, cancellationToken);
        foreach (var connection in (await accounts.GetAccountsAsync(cancellationToken)).Where(a => a.CloudId == cloudId).SelectMany(a => a.Connections))
        {
            await credentials.SetSecretAsync(connection.Id, password, cancellationToken);
        }
    }
}
