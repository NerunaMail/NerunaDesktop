using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg.OpenPgp;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Security;

/// <summary>The PGP keys in use: public key rings to verify and encrypt, own secret ones with their passphrase.</summary>
public sealed record PgpKeyring(IReadOnlyList<(PgpPublicKeyRing Ring, PgpKeyInfo Info)> Public, IReadOnlyList<(PgpSecretKeyRing Ring, string Passphrase, PgpKeyInfo Info)> Secret);

/// <summary>
/// The user's PGP keys, like <see cref="CertificateManager"/> for S/MIME: kept in the database (secret keys stay
/// protected by their passphrase), the passphrases in the system's key store – never in the database or the logs.
/// </summary>
/// <param name="contacts">Keys kept with contacts (vCard KEY); null: none.</param>
public sealed class PgpKeyManager(IPgpKeyStore store, ICredentialStore secrets, TimeProvider clock, IContactPgpKeys? contacts = null)
{
    /// <summary>Keys were imported, made, confirmed or removed.</summary>
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<PgpKeyInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var local = await store.GetAllAsync(cancellationToken);
        return [.. local.Select(Describe).OfType<PgpKeyInfo>()
            .Concat((await ContactKeysAsync(local, cancellationToken)).Select(c => c.Info))
            .OrderByDescending(k => k.HasSecretKey)
            .ThenBy(k => k.EmailAddresses.FirstOrDefault() ?? k.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(k => k.Created)];
    }

    /// <summary>
    /// Imports the keys in an .asc/.gpg/.pgp file: public keys of others, or own keys with their secret part – then
    /// <paramref name="passphrase"/> must open them; it is kept in the system's key store.
    /// </summary>
    /// <exception cref="CertificatePasswordException">Secret keys without or with a wrong passphrase.</exception>
    /// <exception cref="FormatException">Not an OpenPGP key.</exception>
    public async Task<IReadOnlyList<PgpKeyInfo>> ImportAsync(byte[] data, string? passphrase, PgpKeySource source = PgpKeySource.Imported, bool verified = false,
        CancellationToken cancellationToken = default)
    {
        var (publicRings, secretRings) = PgpKeyParser.Read(data);
        foreach (var ring in secretRings.Where(r => r.GetSecretKeys().Any(k => !k.IsPrivateKeyEmpty)))
        {
            if (!PgpKeyParser.Opens(ring, passphrase ?? string.Empty))
            {
                throw new CertificatePasswordException(passphrase is null
                    ? T("Die Datei enthält einen privaten Schlüssel. Bitte die Passphrase eingeben.")
                    : T("Die Passphrase ist falsch."));
            }
        }

        var result = new List<PgpKeyInfo>();
        foreach (var ring in secretRings)
        {
            var fingerprint = PgpKeyParser.FingerprintOf(ring.GetPublicKey());
            await secrets.SetSecretAsync(SecretId(fingerprint), passphrase ?? string.Empty, cancellationToken);
            result.Add(await SaveAsync(PgpKeyParser.PublicOf(ring), ring, source, verified, cancellationToken));
        }

        foreach (var ring in publicRings.Where(p => result.All(r => r.Fingerprint != PgpKeyParser.FingerprintOf(p))))
        {
            result.Add(await SaveAsync(ring, null, source, verified, cancellationToken));
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>A new own key (Ed25519/Curve25519, three years), its random passphrase kept in the system's key store.</summary>
    public async Task<PgpKeyInfo> GenerateAsync(string? name, string email, CancellationToken cancellationToken = default)
    {
        var passphrase = PgpKeyGenerator.RandomPassphrase();
        var ring = await Task.Run(() => PgpKeyGenerator.Generate(name, email, passphrase, clock.GetUtcNow().UtcDateTime), cancellationToken);
        var fingerprint = PgpKeyParser.FingerprintOf(ring.GetPublicKey());
        await secrets.SetSecretAsync(SecretId(fingerprint), passphrase, cancellationToken);
        var info = await SaveAsync(PgpKeyParser.PublicOf(ring), ring, PgpKeySource.Generated, true, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    /// <summary>
    /// A public key seen in a mail (Autocrypt) or found for a recipient (WKD, key server): kept as known, not confirmed –
    /// never over a key that is trusted more, and never replacing its trust.
    /// </summary>
    public async Task<PgpKeyInfo?> RememberAsync(PgpPublicKeyRing ring, PgpKeySource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var info = await SaveAsync(ring, null, source, false, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    /// <summary>
    /// The user compared the fingerprint: the key is confirmed and, where a contact with its address can take it, kept
    /// with the contact too (it then reaches the user's other devices).
    /// </summary>
    /// <returns>Whether a contact has the key now.</returns>
    public async Task<bool> ConfirmAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        await SetVerifiedAsync(fingerprint, true, cancellationToken);
        if (contacts is null || await StoredAsync(fingerprint, cancellationToken) is not { SecretKeyRing: null } stored)
        {
            return false;
        }

        var info = Describe(stored);
        var added = info is not null && await contacts.AddAsync(stored.PublicKeyRing, info.EmailAddresses, cancellationToken);
        if (added)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    /// <summary>The user compared the fingerprint (or takes it back).</summary>
    public async Task SetVerifiedAsync(string fingerprint, bool verified, CancellationToken cancellationToken = default)
    {
        if ((await store.GetAllAsync(cancellationToken)).FirstOrDefault(k => k.Fingerprint == fingerprint) is { } stored)
        {
            await store.SaveAsync(stored with { Verified = verified }, cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Removes the key from this device and from the contacts that have it.</summary>
    public async Task RemoveAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        if (contacts is not null)
        {
            foreach (var (key, _) in (await contacts.GetAllAsync(cancellationToken)).Where(c => string.Equals(FingerprintOf(c.Key), fingerprint, StringComparison.OrdinalIgnoreCase)))
            {
                await contacts.RemoveAsync(key, cancellationToken);
            }
        }

        await store.DeleteAsync(fingerprint, cancellationToken);
        await secrets.DeleteSecretAsync(SecretId(fingerprint), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The public key as .asc text, to give to others.</summary>
    public async Task<string> ExportPublicAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        if (await StoredAsync(fingerprint, cancellationToken) is { } stored)
        {
            return PgpKeyParser.Armor(new PgpPublicKeyRing(stored.PublicKeyRing));
        }

        return (await ContactKeysAsync([], cancellationToken)).FirstOrDefault(c => string.Equals(c.Info.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) is { Ring: { } ring }
            ? PgpKeyParser.Armor(ring)
            : throw NotFound();
    }

    /// <summary>The own key with its secret part as .asc text, protected by <paramref name="passphrase"/> (a backup).</summary>
    public async Task<string> ExportSecretAsync(string fingerprint, string passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        var stored = await StoredAsync(fingerprint, cancellationToken);
        if (stored?.SecretKeyRing is not { } secret || await secrets.GetSecretAsync(SecretId(fingerprint), cancellationToken) is not { } current)
        {
            throw NotFound();
        }

        var ring = PgpKeyParser.WithPassphrase(new PgpSecretKeyRing(secret), current, passphrase);
        return PgpKeyParser.Armor(new PgpPublicKeyRing(stored.PublicKeyRing)) + PgpKeyParser.Armor(ring);
    }

    /// <summary>Everything to verify, decrypt, sign and encrypt with; secret keys only when their passphrase is known.</summary>
    public async Task<PgpKeyring> LoadKeyringAsync(CancellationToken cancellationToken = default)
    {
        var publicRings = new List<(PgpPublicKeyRing, PgpKeyInfo)>();
        var secretRings = new List<(PgpSecretKeyRing, string, PgpKeyInfo)>();
        var all = await store.GetAllAsync(cancellationToken);
        publicRings.AddRange(await ContactKeysAsync(all, cancellationToken));
        foreach (var stored in all)
        {
            PgpPublicKeyRing ring;
            try
            {
                ring = new PgpPublicKeyRing(stored.PublicKeyRing);
            }
            catch (Exception ex) when (ex is IOException or PgpException)
            {
                continue;
            }

            var info = PgpKeyParser.Describe(ring, stored.SecretKeyRing is not null, stored.Source, stored.Verified, stored.AddedAt);
            publicRings.Add((ring, info));
            if (stored.SecretKeyRing is { } secret && await secrets.GetSecretAsync(SecretId(stored.Fingerprint), cancellationToken) is { } passphrase)
            {
                try
                {
                    secretRings.Add((new PgpSecretKeyRing(secret), passphrase, info));
                }
                catch (Exception ex) when (ex is IOException or PgpException)
                {
                    // An unreadable secret key: the key stays usable to verify and encrypt.
                }
            }
        }

        return new PgpKeyring(publicRings, secretRings);
    }

    /// <summary>
    /// The user's own and collected keys for a settings backup, with the passphrases of the own ones – never those of
    /// the organisation (they come back through the cloud).
    /// </summary>
    public async Task<IReadOnlyList<(StoredPgpKey Key, string? Passphrase)>> GetPersonalAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<(StoredPgpKey, string?)>();
        foreach (var key in (await store.GetAllAsync(cancellationToken)).Where(k => k.Source is not (PgpKeySource.Cloud or PgpKeySource.Organization))
                     .OrderBy(k => k.Fingerprint, StringComparer.Ordinal))
        {
            result.Add((key, key.SecretKeyRing is null ? null : await secrets.GetSecretAsync(SecretId(key.Fingerprint), cancellationToken)));
        }

        return result;
    }

    /// <summary>Makes the personal keys those of a backup (the organisation's stay).</summary>
    public async Task ReplacePersonalAsync(IReadOnlyList<(StoredPgpKey Key, string? Passphrase)> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var keep = keys.Select(k => k.Key.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in (await store.GetAllAsync(cancellationToken)).Where(k => k.Source is not (PgpKeySource.Cloud or PgpKeySource.Organization) && !keep.Contains(k.Fingerprint)))
        {
            await store.DeleteAsync(stale.Fingerprint, cancellationToken);
            await secrets.DeleteSecretAsync(SecretId(stale.Fingerprint), cancellationToken);
        }

        foreach (var (key, passphrase) in keys)
        {
            await store.SaveAsync(key, cancellationToken);
            if (key.SecretKeyRing is not null && passphrase is not null)
            {
                await secrets.SetSecretAsync(SecretId(key.Fingerprint), passphrase, cancellationToken);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<PgpKeyInfo> SaveAsync(PgpPublicKeyRing ring, PgpSecretKeyRing? secret, PgpKeySource source, bool verified, CancellationToken cancellationToken)
    {
        var fingerprint = PgpKeyParser.FingerprintOf(ring);
        var now = clock.GetUtcNow();
        var existing = await StoredAsync(fingerprint, cancellationToken);
        var publicRing = existing is null ? ring : Merge(new PgpPublicKeyRing(existing.PublicKeyRing), ring);

        // The stronger source stays (an own key seen again in a mail is still own); confirmation is never taken back here.
        var keptSource = existing is null || Rank(source) < Rank(existing.Source) ? source : existing.Source;
        var stored = new StoredPgpKey(fingerprint, publicRing.GetEncoded(), secret?.GetEncoded() ?? existing?.SecretKeyRing, keptSource,
            verified || existing?.Verified == true, existing?.AddedAt ?? now);
        await store.SaveAsync(stored, cancellationToken);
        return PgpKeyParser.Describe(publicRing, stored.SecretKeyRing is not null, stored.Source, stored.Verified, stored.AddedAt);
    }

    // A newer copy of the same key brings new signatures (extended validity, revocation, more user IDs): both kept.
    private static PgpPublicKeyRing Merge(PgpPublicKeyRing known, PgpPublicKeyRing seen)
    {
        try
        {
            var merged = known;
            foreach (var key in seen.GetPublicKeys())
            {
                var mine = merged.GetPublicKey(key.KeyId);
                merged = mine is null
                    ? PgpPublicKeyRing.InsertPublicKey(merged, key)
                    : PgpPublicKeyRing.InsertPublicKey(PgpPublicKeyRing.RemovePublicKey(merged, mine) ?? merged, PgpPublicKey.Join(mine, key, true, true));
            }

            return merged;
        }
        catch (Exception ex) when (ex is ArgumentException or PgpException or InvalidOperationException)
        {
            return seen;
        }
    }

    private static int Rank(PgpKeySource source) => source switch
    {
        PgpKeySource.Generated => 0,
        PgpKeySource.Cloud => 1,
        PgpKeySource.Organization => 2,
        PgpKeySource.Imported => 3,
        PgpKeySource.Contact => 4,
        PgpKeySource.KeyServer => 5,
        _ => 6,
    };

    private async Task<StoredPgpKey?> StoredAsync(string fingerprint, CancellationToken cancellationToken) =>
        (await store.GetAllAsync(cancellationToken)).FirstOrDefault(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    private static PgpKeyInfo? Describe(StoredPgpKey stored)
    {
        try
        {
            return PgpKeyParser.Describe(new PgpPublicKeyRing(stored.PublicKeyRing), stored.SecretKeyRing is not null, stored.Source, stored.Verified, stored.AddedAt);
        }
        catch (Exception ex) when (ex is IOException or PgpException)
        {
            return null;
        }
    }

    // The contacts' keys that can be read and are not also kept here (those count as here, with their own trust).
    private async Task<IReadOnlyList<(PgpPublicKeyRing Ring, PgpKeyInfo Info)>> ContactKeysAsync(IReadOnlyList<StoredPgpKey> local, CancellationToken cancellationToken)
    {
        if (contacts is null)
        {
            return [];
        }

        var result = new List<(PgpPublicKeyRing, PgpKeyInfo)>();
        foreach (var (key, _) in await contacts.GetAllAsync(cancellationToken))
        {
            try
            {
                foreach (var ring in PgpKeyParser.Read(key).Public)
                {
                    var fingerprint = PgpKeyParser.FingerprintOf(ring);
                    if (local.All(l => l.Fingerprint != fingerprint) && result.All(r => r.Item2.Fingerprint != fingerprint))
                    {
                        result.Add((ring, PgpKeyParser.Describe(ring, false, PgpKeySource.Contact, true, DateTimeOffset.MinValue)));
                    }
                }
            }
            catch (FormatException)
            {
                // A broken KEY in a contact is not a reason to fail.
            }
        }

        return result;
    }

    private static string? FingerprintOf(byte[] key)
    {
        try
        {
            return PgpKeyParser.Read(key).Public.Select(PgpKeyParser.FingerprintOf).FirstOrDefault();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static InvalidOperationException NotFound() => new(T("Schlüssel nicht gefunden."));

    private static Guid SecretId(string fingerprint) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("neruna-pgp:" + fingerprint.ToUpperInvariant())).AsSpan(0, 16));
}
