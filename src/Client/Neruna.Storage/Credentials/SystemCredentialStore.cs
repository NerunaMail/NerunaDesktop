using Microsoft.Extensions.Logging;
using Neruna.Core.Security;

namespace Neruna.Storage.Credentials;

/// <summary>One secret per key in the operating system's credential store.</summary>
internal interface ISecretBackend
{
    /// <summary>Shown in the log and in Einstellungen → Info.</summary>
    string Name { get; }

    string? Get(string key);

    void Set(string key, string secret);

    void Delete(string key);
}

/// <summary>
/// Passwords and other secrets in the operating system's credential store: Windows Credential Manager, macOS Keychain
/// or the Secret Service on Linux (GNOME Keyring, KWallet). Each secret is an entry "Neruna" / connection ID.
/// </summary>
internal sealed class SystemCredentialStore(ISecretBackend backend) : ICredentialStore
{
    public string Location => backend.Name;

    public Task<string?> GetSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        Task.Run(() => backend.Get(Key(connectionId)), cancellationToken);

    public Task SetSecretAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Task.Run(() => backend.Set(Key(connectionId), secret), cancellationToken);
    }

    public Task DeleteSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        Task.Run(() => backend.Delete(Key(connectionId)), cancellationToken);

    /// <summary>
    /// Moves the secrets of the interim file store (<c>credentials.json</c>) into the system store. Each one is read back
    /// before the file is deleted; if anything fails, the file stays and is tried again at the next start.
    /// </summary>
    /// <returns>How many secrets were moved.</returns>
    public async Task<int> MoveFromAsync(LocalCredentialStore file, ILogger logger, CancellationToken cancellationToken)
    {
        if (!file.Exists)
        {
            return 0;
        }

        var secrets = await file.ReadAllAsync(cancellationToken);
        foreach (var (id, secret) in secrets)
        {
            await SetSecretAsync(id, secret, cancellationToken);
            if (await GetSecretAsync(id, cancellationToken) != secret)
            {
                logger.LogWarning("Secret {Id} could not be verified in {Backend}; keeping credentials.json", id, Location);
                return 0;
            }
        }

        file.DeleteFiles();
        logger.LogInformation("Moved {Count} secrets from credentials.json to {Backend}", secrets.Count, backend.Name);
        return secrets.Count;
    }

    internal static string Key(Guid connectionId) => connectionId.ToString("N");
}

/// <summary>Picks the credential store for this computer.</summary>
internal static class CredentialStoreSelector
{
    public const string Service = "Neruna";

    /// <summary>The system store if there is one that works, otherwise null (then the encrypted file is used).</summary>
    public static SystemCredentialStore? TrySystemStore(ILogger logger)
    {
        try
        {
            ISecretBackend? backend =
                OperatingSystem.IsWindows() ? new ChunkingSecretBackend(new WindowsCredentialBackend(), WindowsCredentialBackend.MaxChars) :
                OperatingSystem.IsMacOS() ? new MacKeychainBackend() :
                OperatingSystem.IsLinux() ? LibSecretBackend.TryCreate(logger) :
                null;
            if (backend is null)
            {
                return null;
            }

            // A lookup that finds nothing proves the store is reachable (and, on Linux, unlocked).
            var probe = Task.Run(() => backend.Get("probe-" + Guid.Empty.ToString("N")));
            if (!probe.Wait(TimeSpan.FromSeconds(10)))
            {
                logger.LogWarning("{Backend} did not answer; using the encrypted file instead", backend.Name);
                return null;
            }

            logger.LogInformation("Secrets are stored in {Backend}", backend.Name);
            return new SystemCredentialStore(backend);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "No system credential store available; using the encrypted file instead");
            return null;
        }
    }
}
