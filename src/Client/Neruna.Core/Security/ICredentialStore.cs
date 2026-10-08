namespace Neruna.Core.Security;

/// <summary>
/// Stores secrets (passwords, OAuth tokens) per service connection. Never backed by the SQLite database.
/// Target implementations: Windows Credential Manager, macOS Keychain, Linux Secret Service (libsecret).
/// </summary>
public interface ICredentialStore
{
    /// <summary>Where the secrets are kept, for the user ("Windows-Anmeldeinformationsverwaltung" …).</summary>
    string Location { get; }

    Task<string?> GetSecretAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task SetSecretAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default);

    Task DeleteSecretAsync(Guid connectionId, CancellationToken cancellationToken = default);
}
