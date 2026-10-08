using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Storage.Credentials;

namespace Neruna.Client.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Passwords_from_the_file_move_into_the_system_store_and_the_file_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = Guid.NewGuid();
        var certificate = Guid.NewGuid();
        using var file = new LocalCredentialStore(_directory);
        await file.SetSecretAsync(account, "geheim – mit Ümläut", ct);
        await file.SetSecretAsync(certificate, string.Empty, ct);

        var backend = new MemoryBackend();
        var store = new SystemCredentialStore(backend);
        var moved = await store.MoveFromAsync(file, NullLogger.Instance, ct);

        Assert.Equal(2, moved);
        Assert.Equal("geheim – mit Ümläut", await store.GetSecretAsync(account, ct));
        Assert.Equal(string.Empty, await store.GetSecretAsync(certificate, ct));
        Assert.False(File.Exists(Path.Combine(_directory, "credentials.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "credentials.key")));

        // Nothing left to move at the next start.
        Assert.Equal(0, await store.MoveFromAsync(file, NullLogger.Instance, ct));
    }

    [Fact]
    public async Task File_is_kept_when_the_system_store_does_not_return_what_was_written()
    {
        var ct = TestContext.Current.CancellationToken;
        using var file = new LocalCredentialStore(_directory);
        await file.SetSecretAsync(Guid.NewGuid(), "geheim", ct);

        var moved = await new SystemCredentialStore(new MemoryBackend { Broken = true }).MoveFromAsync(file, NullLogger.Instance, ct);

        Assert.Equal(0, moved);
        Assert.True(File.Exists(Path.Combine(_directory, "credentials.json")));
    }

    /// <summary>
    /// Against the real credential store of this computer. Only when asked for (NERUNA_TEST_KEYRING=1), so tests never
    /// write into a developer's keychain unasked; uses a random ID and removes it again.
    /// </summary>
    [Fact]
    public async Task System_store_of_this_computer_stores_overwrites_and_deletes()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("NERUNA_TEST_KEYRING") != "1", "NERUNA_TEST_KEYRING not set");
        var ct = TestContext.Current.CancellationToken;

        var store = CredentialStoreSelector.TrySystemStore(NullLogger.Instance);
        Assert.NotNull(store);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null(await store.GetSecretAsync(id, ct));
            await store.SetSecretAsync(id, "erstes Passwort", ct);
            await store.SetSecretAsync(id, "zweites – Pässwort ✓", ct);
            Assert.Equal("zweites – Pässwort ✓", await store.GetSecretAsync(id, ct));
            await store.SetSecretAsync(id, string.Empty, ct);
            Assert.Equal(string.Empty, await store.GetSecretAsync(id, ct));
        }
        finally
        {
            await store.DeleteSecretAsync(id, ct);
        }

        Assert.Null(await store.GetSecretAsync(id, ct));
        await store.DeleteSecretAsync(id, ct); // deleting twice is fine
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class MemoryBackend : ISecretBackend
    {
        private readonly Dictionary<string, string> _secrets = [];

        public bool Broken { get; init; }

        public string Name => "Speicher";

        public string? Get(string key) => Broken ? null : _secrets.GetValueOrDefault(key);

        public void Set(string key, string secret) => _secrets[key] = secret;

        public void Delete(string key) => _secrets.Remove(key);
    }
}
