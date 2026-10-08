using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neruna.Core.Security;

namespace Neruna.Storage.Credentials;

/// <summary>
/// Fallback credential store where the operating system has none (e.g. Linux without Secret Service), and for tests:
/// secrets encrypted with AES-256-GCM in <c>credentials.json</c>, key in a user-only file next to it. This keeps
/// passwords out of the database and out of backups of the DB alone, but anyone with access to the user's profile
/// can decrypt them. Normally <see cref="SystemCredentialStore"/> is used.
/// </summary>
public sealed class LocalCredentialStore : ICredentialStore, IDisposable
{
    private readonly string _storePath;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LocalCredentialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _storePath = Path.Combine(directory, "credentials.json");
        _keyPath = Path.Combine(directory, "credentials.key");
    }

    public async Task<string?> GetSecretAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(cancellationToken);
            if (!entries.TryGetValue(connectionId.ToString("N"), out var entry))
            {
                return null;
            }

            var key = await GetKeyAsync(cancellationToken);
            var nonce = Convert.FromBase64String(entry.Nonce);
            var data = Convert.FromBase64String(entry.Ciphertext);
            var plaintext = new byte[data.Length - 16];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, data.AsSpan(0, plaintext.Length), data.AsSpan(plaintext.Length), plaintext, connectionId.ToByteArray());
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetSecretAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var key = await GetKeyAsync(cancellationToken);
            var plaintext = Encoding.UTF8.GetBytes(secret);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var data = new byte[plaintext.Length + 16];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plaintext, data.AsSpan(0, plaintext.Length), data.AsSpan(plaintext.Length), connectionId.ToByteArray());
            }

            var entries = await LoadAsync(cancellationToken);
            entries[connectionId.ToString("N")] = new Entry(Convert.ToBase64String(nonce), Convert.ToBase64String(data));
            await SaveAsync(entries, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteSecretAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(cancellationToken);
            if (entries.Remove(connectionId.ToString("N")))
            {
                await SaveAsync(entries, cancellationToken);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    public bool Exists => File.Exists(_storePath);

    public string Location => "verschlüsselte Datei im Datenordner (kein Schlüsselbund des Systems verfügbar)";

    /// <summary>Every stored secret, to move them into the system store.</summary>
    internal async Task<IReadOnlyDictionary<Guid, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in (await LoadAsync(cancellationToken)).Keys)
        {
            if (Guid.TryParseExact(id, "N", out var guid) && await GetSecretAsync(guid, cancellationToken) is { } secret)
            {
                result[guid] = secret;
            }
        }

        return result;
    }

    /// <summary>Removes the file and its key once everything lives in the system store.</summary>
    internal void DeleteFiles()
    {
        File.Delete(_storePath);
        File.Delete(_keyPath);
    }

    private async Task<Dictionary<string, Entry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_storePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, Entry>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    private async Task SaveAsync(Dictionary<string, Entry> entries, CancellationToken cancellationToken)
    {
        var temp = _storePath + ".tmp";
        await using (var stream = CreateUserOnlyFile(temp))
        {
            await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: cancellationToken);
        }

        File.Move(temp, _storePath, overwrite: true);
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_keyPath))
        {
            return await File.ReadAllBytesAsync(_keyPath, cancellationToken);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        await using (var stream = CreateUserOnlyFile(_keyPath))
        {
            await stream.WriteAsync(key, cancellationToken);
        }

        return key;
    }

    private static FileStream CreateUserOnlyFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private sealed record Entry(string Nonce, string Ciphertext);
}
