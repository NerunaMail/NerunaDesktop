namespace Neruna.Vault;

/// <summary>
/// The only form in which a vault ever leaves the client. A server stores it as an opaque document
/// and cannot decrypt it: it has neither the password, the recovery code nor the data key.
/// </summary>
/// <param name="Version">Format version, see <see cref="VaultFormat.CurrentVersion"/>.</param>
/// <param name="VaultId">Stable id; bound into every ciphertext so envelopes cannot be swapped between vaults.</param>
/// <param name="Keys">The data key, wrapped once per unlock method (password, recovery code, …).</param>
/// <param name="Nonce">Base64 AES-GCM nonce of the payload.</param>
/// <param name="Ciphertext">Base64 AES-GCM ciphertext of the payload, with the 16-byte tag appended.</param>
public sealed record VaultEnvelope(
    int Version,
    Guid VaultId,
    IReadOnlyList<WrappedKey> Keys,
    string Nonce,
    string Ciphertext);

/// <param name="Kind">See <see cref="WrappedKeyKind"/>.</param>
/// <param name="Salt">Base64 KDF salt; only for password-derived keys.</param>
/// <param name="Kdf">KDF parameters; only for password-derived keys.</param>
public sealed record WrappedKey(
    string Kind,
    string? Salt,
    KdfParameters? Kdf,
    string Nonce,
    string Ciphertext);

public static class WrappedKeyKind
{
    public const string Password = "password";
    public const string Recovery = "recovery";
}

/// <summary>Argon2id parameters. Stored with the envelope so they can be raised later without breaking old vaults.</summary>
public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    public const string Algorithm = "argon2id";

    /// <summary>OWASP-recommended baseline as of 2025: 64 MiB, 3 passes, 1 lane.</summary>
    public static KdfParameters Default { get; } = new(64 * 1024, 3, 1);

    /// <summary>Lower bound accepted when unlocking, so a tampered envelope cannot make us derive a weak key.</summary>
    public static KdfParameters Minimum { get; } = new(19 * 1024, 2, 1);

    internal bool IsAtLeast(KdfParameters other) =>
        MemoryKiB >= other.MemoryKiB && Iterations >= other.Iterations && Parallelism >= other.Parallelism;
}

public static class VaultFormat
{
    public const int CurrentVersion = 1;
}
