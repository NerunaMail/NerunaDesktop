using Neruna.Storage.Credentials;

namespace Neruna.Client.Tests;

/// <summary>Windows keeps 1280 characters per credential; sign-in tokens are longer (crash in 0.1.11).</summary>
public class ChunkingSecretBackendTests
{
    [Fact]
    public void Long_secrets_are_split_short_ones_stay_as_they_were()
    {
        var store = new MemoryBackend(maxChars: 16); // room for the "neruna-parts:n" header
        var backend = new ChunkingSecretBackend(store, 10);

        backend.Set("pw", "geheim");
        Assert.Equal("geheim", store.Entries["pw"]);
        Assert.Equal("geheim", backend.Get("pw"));

        var token = new string('x', 25) + "ende";
        backend.Set("token", token);
        Assert.Equal(token, backend.Get("token"));
        Assert.Equal(["token", "token#1", "token#2", "token#3"], store.Entries.Keys.Where(k => k.StartsWith("token", StringComparison.Ordinal)).Order());
        Assert.All(store.Entries.Where(e => e.Key.Contains('#', StringComparison.Ordinal)), e => Assert.True(e.Value.Length <= 10));

        // Shorter again: the parts no longer needed go.
        backend.Set("token", "kurz");
        Assert.Equal("kurz", backend.Get("token"));
        Assert.DoesNotContain("token#1", store.Entries.Keys);

        backend.Set("token", token);
        backend.Delete("token");
        Assert.Null(backend.Get("token"));
        Assert.DoesNotContain(store.Entries.Keys, k => k.StartsWith("token", StringComparison.Ordinal));
    }

    private sealed class MemoryBackend(int maxChars) : ISecretBackend
    {
        public Dictionary<string, string> Entries { get; } = [];

        public string Name => "memory";

        public string? Get(string key) => Entries.GetValueOrDefault(key);

        public void Set(string key, string secret) =>
            Entries[key] = secret.Length <= maxChars ? secret : throw new InvalidOperationException("The stub received bad data.");

        public void Delete(string key) => Entries.Remove(key);
    }
}
