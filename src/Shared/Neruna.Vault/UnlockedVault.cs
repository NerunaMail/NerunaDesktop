using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Neruna.Vault;

/// <summary>
/// A vault whose data key (DEK) is in memory. Created fresh or by unlocking an envelope with the
/// password or the recovery code; then seals and opens payloads. Dispose to wipe the key.
/// </summary>
/// <remarks>
/// Scheme (see docs/architecture.md, section 5):
/// <list type="bullet">
/// <item>DEK: 256 random bits, encrypts the payload with AES-256-GCM.</item>
/// <item>Password → Argon2id(salt, params) → KEK, wraps the DEK with AES-256-GCM.</item>
/// <item>Recovery code: 256 random bits → HKDF-SHA256 → KEK, wraps the DEK.</item>
/// <item>Every ciphertext authenticates version, vault id and (for keys) kind + KDF params as AAD.</item>
/// </list>
/// </remarks>
public sealed class UnlockedVault : IDisposable
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;

    private readonly byte[] _dek;
    private readonly List<WrappedKey> _keys;
    private bool _disposed;

    private UnlockedVault(Guid vaultId, byte[] dek, IEnumerable<WrappedKey> keys)
    {
        VaultId = vaultId;
        _dek = dek;
        _keys = [.. keys];
    }

    public Guid VaultId { get; }

    /// <summary>Creates a new vault protected by <paramref name="password"/>.</summary>
    /// <param name="recoveryCode">Shown to the user exactly once; it is the only way back if the password is lost.</param>
    public static UnlockedVault Create(string password, out string recoveryCode, KdfParameters? kdf = null)
    {
        var vault = new UnlockedVault(Guid.NewGuid(), RandomNumberGenerator.GetBytes(KeySize), []);
        vault.SetPassword(password, kdf ?? KdfParameters.Default);
        recoveryCode = vault.RegenerateRecoveryCode();
        return vault;
    }

    /// <exception cref="VaultUnlockException">Wrong password or tampered envelope.</exception>
    public static UnlockedVault UnlockWithPassword(VaultEnvelope envelope, string password, KdfParameters? minimumKdf = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(password);
        EnsureSupported(envelope);

        var wrapped = envelope.Keys.FirstOrDefault(k => k.Kind == WrappedKeyKind.Password)
            ?? throw new VaultUnlockException("The vault has no password key.");
        if (wrapped.Kdf is null || wrapped.Salt is null || !wrapped.Kdf.IsAtLeast(minimumKdf ?? KdfParameters.Minimum))
        {
            throw new VaultUnlockException("The vault's key derivation parameters are missing or too weak.");
        }

        var kek = DerivePasswordKey(password, Convert.FromBase64String(wrapped.Salt), wrapped.Kdf);
        return Unwrap(envelope, wrapped, kek);
    }

    /// <exception cref="VaultUnlockException">Wrong code or tampered envelope.</exception>
    public static UnlockedVault UnlockWithRecoveryCode(VaultEnvelope envelope, string recoveryCode)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        EnsureSupported(envelope);

        var wrapped = envelope.Keys.FirstOrDefault(k => k.Kind == WrappedKeyKind.Recovery)
            ?? throw new VaultUnlockException("The vault has no recovery key.");

        byte[] secret;
        try
        {
            secret = RecoveryCode.Decode(recoveryCode);
        }
        catch (FormatException ex)
        {
            throw new VaultUnlockException("The recovery code is malformed.", ex);
        }

        return Unwrap(envelope, wrapped, DeriveRecoveryKey(secret));
    }

    /// <summary>Encrypts <paramref name="payload"/> into a new envelope (fresh nonce every time).</summary>
    public VaultEnvelope Seal(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (nonce, ciphertext) = Encrypt(_dek, payload, PayloadAad(VaultFormat.CurrentVersion, VaultId));
        return new VaultEnvelope(VaultFormat.CurrentVersion, VaultId, [.. _keys], nonce, ciphertext);
    }

    /// <exception cref="VaultUnlockException">The envelope belongs to another vault or was tampered with.</exception>
    public byte[] Open(VaultEnvelope envelope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(envelope);
        EnsureSupported(envelope);
        if (envelope.VaultId != VaultId)
        {
            throw new VaultUnlockException("The envelope belongs to a different vault.");
        }

        return Decrypt(_dek, envelope.Nonce, envelope.Ciphertext, PayloadAad(envelope.Version, envelope.VaultId))
            ?? throw new VaultUnlockException("The vault payload failed authentication.");
    }

    /// <summary>Replaces the password. Takes effect with the next <see cref="Seal"/>; the payload key is unchanged.</summary>
    public void SetPassword(string password, KdfParameters? kdf = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(password);
        kdf ??= KdfParameters.Default;

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var kek = DerivePasswordKey(password, salt, kdf);
        try
        {
            var (nonce, ciphertext) = Encrypt(kek, _dek, KeyAad(VaultId, WrappedKeyKind.Password, kdf));
            ReplaceKey(new WrappedKey(WrappedKeyKind.Password, Convert.ToBase64String(salt), kdf, nonce, ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Invalidates the previous recovery code and returns a new one.</summary>
    public string RegenerateRecoveryCode()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var secret = RandomNumberGenerator.GetBytes(KeySize);
        var kek = DeriveRecoveryKey(secret);
        try
        {
            var (nonce, ciphertext) = Encrypt(kek, _dek, KeyAad(VaultId, WrappedKeyKind.Recovery, null));
            ReplaceKey(new WrappedKey(WrappedKeyKind.Recovery, null, null, nonce, ciphertext));
            return RecoveryCode.Encode(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_dek);
            _disposed = true;
        }
    }

    private void ReplaceKey(WrappedKey key)
    {
        _keys.RemoveAll(k => k.Kind == key.Kind);
        _keys.Add(key);
    }

    private static UnlockedVault Unwrap(VaultEnvelope envelope, WrappedKey wrapped, byte[] kek)
    {
        try
        {
            var dek = Decrypt(kek, wrapped.Nonce, wrapped.Ciphertext, KeyAad(envelope.VaultId, wrapped.Kind, wrapped.Kdf))
                ?? throw new VaultUnlockException("Wrong password or recovery code.");
            return new UnlockedVault(envelope.VaultId, dek, envelope.Keys);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static void EnsureSupported(VaultEnvelope envelope)
    {
        if (envelope.Version != VaultFormat.CurrentVersion)
        {
            throw new VaultUnlockException($"Unsupported vault format version {envelope.Version}.");
        }
    }

    private static byte[] DerivePasswordKey(string password, byte[] salt, KdfParameters kdf)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = kdf.MemoryKiB,
                Iterations = kdf.Iterations,
                DegreeOfParallelism = kdf.Parallelism,
            };
            return argon.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static byte[] DeriveRecoveryKey(byte[] secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, salt: [], info: "neruna-vault/recovery"u8.ToArray());

    private static byte[] PayloadAad(int version, Guid vaultId) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"neruna-vault|v{version}|{vaultId:N}|payload"));

    private static byte[] KeyAad(Guid vaultId, string kind, KdfParameters? kdf) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"neruna-vault|v{VaultFormat.CurrentVersion}|{vaultId:N}|key|{kind}|{KdfParameters.Algorithm}:{kdf?.MemoryKiB}:{kdf?.Iterations}:{kdf?.Parallelism}"));

    private static (string Nonce, string Ciphertext) Encrypt(byte[] key, ReadOnlySpan<byte> plaintext, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), aad);
        return (Convert.ToBase64String(nonce), Convert.ToBase64String(output));
    }

    private static byte[]? Decrypt(byte[] key, string nonceBase64, string ciphertextBase64, byte[] aad)
    {
        byte[] nonce, input;
        try
        {
            nonce = Convert.FromBase64String(nonceBase64);
            input = Convert.FromBase64String(ciphertextBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (nonce.Length != NonceSize || input.Length < TagSize)
        {
            return null;
        }

        var plaintext = new byte[input.Length - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, input.AsSpan(0, plaintext.Length), input.AsSpan(plaintext.Length), plaintext, aad);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }
}

public sealed class VaultUnlockException : Exception
{
    public VaultUnlockException()
    {
    }

    public VaultUnlockException(string message)
        : base(message)
    {
    }

    public VaultUnlockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
