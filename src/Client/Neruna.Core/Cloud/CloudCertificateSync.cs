using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts.Cloud;
using Neruna.Core.Security;
using Neruna.Vault;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>Where the certificates from the organisation stand on this device (for Einstellungen → Cloud).</summary>
/// <param name="Available">The licence includes certificates.</param>
/// <param name="Approved">An admin approved this device in the portal.</param>
/// <param name="Received">Certificates from the organisation on this device.</param>
/// <param name="Waiting">Assigned to this person, not yet delivered to this device.</param>
/// <param name="Problem">Something the user should know (e.g. the organisation key changed), or null.</param>
public sealed record CloudCertificateStatus(bool Available, bool Approved, int Received, int Waiting, string? Problem)
{
    public static CloudCertificateStatus None { get; } = new(false, false, 0, 0, null);
}

/// <summary>
/// Brings the S/MIME certificates and PGP keys the organisation assigned to this person onto this device, zero
/// knowledge: this device's encryption key (in the keychain) opens its envelope; a certificate or key is taken only when
/// the organisation key – remembered the first time – signed it. Also the organisation's directory: the public PGP keys
/// of its people, signed by it, taken as trusted. What is no longer assigned or listed is removed (as with signatures).
/// Each connected organisation on its own: own keys, own signing key, own list of what it delivered.
/// </summary>
public sealed class CloudCertificateSync(
    CloudController cloud,
    CertificateManager certificates,
    ICertificateStore store,
    ICredentialStore credentials,
    ISettingsStore settings,
    ILogger<CloudCertificateSync> logger,
    PgpKeyManager? pgp = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CloudCertificateStatus> _status = new();

    /// <summary>Where the certificates of one organisation stand.</summary>
    public CloudCertificateStatus StatusOf(string connectionId) => _status.GetValueOrDefault(connectionId, CloudCertificateStatus.None);

    public event EventHandler? Changed;

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        foreach (var gone in _status.Keys.Where(id => connections.All(c => c.Id != id)))
        {
            _status.TryRemove(gone, out _);
        }

        var changed = false;
        try
        {
            changed = await CloudSync.EachAsync(connections, c => SyncAsync(c, cancellationToken), logger, cancellationToken);
        }
        finally
        {
            // Also after an error: what no organisation offers any more goes.
            changed |= await RemoveDisconnectedAsync(cancellationToken);
        }

        return changed;
    }

    private async Task<bool> SyncAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        using var deviceKey = await DeviceKeyAsync(credentials, connection, logger, cancellationToken);
        await cloud.SetDeviceEncryptionKeyAsync(connection, CertificateEnvelopes.PublicKeyOf(deviceKey), cancellationToken: cancellationToken);
        var response = await cloud.GetCertificatesAsync(connection, cancellationToken);
        var stored = (await store.GetAllAsync(cancellationToken)).Where(s => s.Source == CertificateSource.Cloud).ToList();
        if (!response.Available)
        {
            _status[connection.Id] = CloudCertificateStatus.None;
            await SetClaimsAsync(connection, [], cancellationToken);
            return false;
        }

        // The organisation's signing key, trusted the first time it is seen.
        var signingKey = response.OrganizationKey?.SigningPublicKey;
        var pinnedKey = connection.SettingKey(SettingKeys.CloudOrganizationSigningKey);
        var pinned = await settings.GetAsync(pinnedKey, cancellationToken);
        if (signingKey is not null && pinned is null)
        {
            await settings.SetAsync(pinnedKey, signingKey, cancellationToken);
            pinned = signingKey;
        }

        if (signingKey is not null && signingKey != pinned)
        {
            logger.LogWarning("The organisation key for certificates changed; no certificates taken");
            _status[connection.Id] = new CloudCertificateStatus(true, response.Device.Approved, 0, response.Certificates.Count,
                T("Der Organisationsschlüssel hat sich geändert – aus Sicherheitsgründen werden keine Zertifikate übernommen. Bitte die Cloud-Verbindung neu einrichten."));
            return false;
        }

        var present = stored.ToDictionary(s => Fingerprint(s.Der), StringComparer.Ordinal);
        var claims = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        var received = 0;
        var waiting = 0;
        foreach (var offered in response.Certificates)
        {
            if (present.ContainsKey(offered.Fingerprint))
            {
                claims.Add(offered.Fingerprint);
                received++;
                continue;
            }

            if (offered.Envelope is null || signingKey is null)
            {
                waiting++;
                continue;
            }

            if (!CertificateEnvelopes.Verify(offered.Id, offered.Payload, offered.PayloadSignature, signingKey))
            {
                logger.LogWarning("Certificate {Id} not signed by the organisation; skipped", offered.Id);
                waiting++;
                continue;
            }

            try
            {
                var key = CertificateEnvelopes.Unwrap(offered.Envelope, deviceKey, offered.Id, connection.DeviceId);
                var (pkcs12, password) = CertificateEnvelopes.Decrypt(offered.Id, offered.Payload, key);
                await certificates.ImportAsync(pkcs12, password, CertificateSource.Cloud, cancellationToken);
                claims.Add(offered.Fingerprint);
                received++;
                changed = true;
                logger.LogInformation("Certificate for {Addresses} received from the organisation", string.Join(", ", offered.EmailAddresses));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or CertificatePasswordException)
            {
                logger.LogWarning(ex, "Certificate {Id} could not be opened", offered.Id);
                waiting++;
            }
        }

        if (pgp is not null && signingKey is not null)
        {
            var (pgpChanged, pgpReceived, pgpWaiting) = await SyncPgpAsync(pgp, connection, deviceKey, signingKey, cancellationToken);
            changed |= pgpChanged;
            received += pgpReceived;
            waiting += pgpWaiting;
        }

        // No longer assigned (or deleted in the portal): no longer this organisation's – gone, unless another one has it.
        await SetClaimsAsync(connection, claims, cancellationToken);
        _status[connection.Id] = new CloudCertificateStatus(true, response.Device.Approved, received, waiting, null);
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>
    /// The person's PGP keys (secret key and passphrase, like a certificate) and the organisation's directory of public
    /// keys, each signed by the organisation. Older servers have none (null).
    /// </summary>
    private async Task<(bool Changed, int Received, int Waiting)> SyncPgpAsync(PgpKeyManager keys, CloudConnection connection, ECDiffieHellman deviceKey,
        string signingKey, CancellationToken cancellationToken)
    {
        var response = await cloud.GetPgpKeysAsync(connection, cancellationToken);
        if (response is null || response.OrganizationKey?.SigningPublicKey != signingKey)
        {
            return (false, 0, 0);
        }

        var stored = await keys.GetAllAsync(cancellationToken);
        var claims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var received = 0;
        var waiting = 0;
        foreach (var offered in response.Keys)
        {
            if (stored.Any(k => k.HasSecretKey && string.Equals(k.Fingerprint, offered.Fingerprint, StringComparison.OrdinalIgnoreCase)))
            {
                claims.Add(offered.Fingerprint);
                received++;
                continue;
            }

            if (offered.Envelope is null)
            {
                waiting++;
                continue;
            }

            if (!CertificateEnvelopes.Verify(offered.Id, offered.Payload, offered.PayloadSignature, signingKey))
            {
                logger.LogWarning("PGP key {Id} not signed by the organisation; skipped", offered.Id);
                waiting++;
                continue;
            }

            try
            {
                var key = CertificateEnvelopes.Unwrap(offered.Envelope, deviceKey, offered.Id, connection.DeviceId);
                var (secretKey, passphrase) = CertificateEnvelopes.DecryptPgp(offered.Id, offered.Payload, key);
                foreach (var imported in await keys.ImportAsync(secretKey, passphrase, PgpKeySource.Cloud, cancellationToken: cancellationToken))
                {
                    claims.Add(imported.Fingerprint);
                }

                received++;
                changed = true;
                logger.LogInformation("PGP key for {Addresses} received from the organisation", string.Join(", ", offered.EmailAddresses));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or CertificatePasswordException or IOException or Org.BouncyCastle.Bcpg.OpenPgp.PgpException)
            {
                logger.LogWarning(ex, "PGP key {Id} could not be opened", offered.Id);
                waiting++;
            }
        }

        // No longer assigned to this person: the secret part goes (as a certificate would); what stays in the
        // directory comes back below as a public key.
        // Only what this organisation gave: another connected one may have assigned keys of its own.
        var assigned = new HashSet<string>(claims, StringComparer.OrdinalIgnoreCase);
        var before = await PgpClaimsAsync(connection, cancellationToken);
        foreach (var lost in stored.Where(k => k.Source == PgpKeySource.Cloud && before.Contains(k.Fingerprint) && !assigned.Contains(k.Fingerprint)))
        {
            await keys.RemoveAsync(lost.Fingerprint, cancellationToken);
            changed = true;
        }

        if (changed)
        {
            stored = await keys.GetAllAsync(cancellationToken);
        }

        // The directory: the organisation's people's public keys, trusted – over keys only seen in mails or online.
        foreach (var entry in response.Directory)
        {
            if (!CertificateEnvelopes.VerifyPgpPublicKey(entry.Id, entry.PublicKey, entry.PublicSignature, signingKey))
            {
                logger.LogWarning("PGP key {Id} in the directory not signed by the organisation; skipped", entry.Id);
                continue;
            }

            try
            {
                var (rings, _) = PgpKeyParser.Read(Convert.FromBase64String(entry.PublicKey));
                if (rings.FirstOrDefault(r => string.Equals(PgpKeyParser.FingerprintOf(r), entry.Fingerprint, StringComparison.OrdinalIgnoreCase)) is not { } ring)
                {
                    continue;
                }

                claims.Add(entry.Fingerprint);
                var known = stored.FirstOrDefault(k => string.Equals(k.Fingerprint, entry.Fingerprint, StringComparison.OrdinalIgnoreCase));
                if (known is null || known.Source is PgpKeySource.Autocrypt or PgpKeySource.KeyServer)
                {
                    await keys.RememberAsync(ring, PgpKeySource.Organization, cancellationToken);
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or Org.BouncyCastle.Bcpg.OpenPgp.PgpException)
            {
                logger.LogWarning(ex, "PGP key {Id} in the directory unreadable", entry.Id);
            }
        }

        await settings.SetAsync(connection.SettingKey(SettingKeys.CloudPgpKeys),
            JsonSerializer.Serialize(claims.Select(c => c.ToUpperInvariant()).Order(StringComparer.Ordinal).ToList()), cancellationToken);
        return (changed, received, waiting);
    }

    /// <summary>
    /// Disconnected (or no longer assigned): the certificates no connected organisation has given this device leave it.
    /// </summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        var pgpGone = await RemoveDisconnectedPgpAsync(cancellationToken);

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var stored = (await store.GetAllAsync(cancellationToken)).Where(s => s.Source == CertificateSource.Cloud).ToList();
        foreach (var connection in await cloud.GetConnectionsAsync(cancellationToken))
        {
            claimed.UnionWith(await ClaimsAsync(connection, stored, cancellationToken));
        }

        var gone = stored.Where(s => !claimed.Contains(Fingerprint(s.Der))).ToList();
        foreach (var certificate in gone)
        {
            await certificates.RemoveAsync(certificate.Thumbprint, cancellationToken);
        }

        if (gone.Count > 0 || pgpGone)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return gone.Count > 0 || pgpGone;
    }

    // The organisation's PGP keys (own ones and the directory) no connected organisation lists any more.
    private async Task<bool> RemoveDisconnectedPgpAsync(CancellationToken cancellationToken)
    {
        if (pgp is null)
        {
            return false;
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in await cloud.GetConnectionsAsync(cancellationToken))
        {
            claimed.UnionWith(await PgpClaimsAsync(connection, cancellationToken));
        }

        var gone = (await pgp.GetAllAsync(cancellationToken))
            .Where(k => k.Source is PgpKeySource.Cloud or PgpKeySource.Organization && !claimed.Contains(k.Fingerprint)).ToList();
        foreach (var key in gone)
        {
            await pgp.RemoveAsync(key.Fingerprint, cancellationToken);
        }

        return gone.Count > 0;
    }

    // Which PGP keys came from this organisation (fingerprints, own keys and directory).
    private async Task<HashSet<string>> PgpClaimsAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(connection.SettingKey(SettingKeys.CloudPgpKeys), cancellationToken);
        try
        {
            return new HashSet<string>(json is null ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Which certificates came from this organisation (fingerprints). Before there could be several, all were the first one's.
    private async Task<IReadOnlyCollection<string>> ClaimsAsync(CloudConnection connection, IReadOnlyList<StoredCertificate> stored, CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(connection.SettingKey(SettingKeys.CloudCertificates), cancellationToken);
        if (json is null)
        {
            return connection.IsFirst ? [.. stored.Select(s => Fingerprint(s.Der))] : [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task SetClaimsAsync(CloudConnection connection, IEnumerable<string> claims, CancellationToken cancellationToken) =>
        settings.SetAsync(connection.SettingKey(SettingKeys.CloudCertificates), JsonSerializer.Serialize(claims.Order(StringComparer.Ordinal).ToList()), cancellationToken);

    private static string Fingerprint(byte[] der) => Convert.ToHexStringLower(SHA256.HashData(der));

    // Made once per device and organisation; the private half only in the system keychain. Shared with the accounts from that organisation.
    public static async Task<ECDiffieHellman> DeviceKeyAsync(ICredentialStore credentials, CloudConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(connection);
        if (await credentials.GetSecretAsync(connection.EncryptionKeyId, cancellationToken) is { Length: > 0 } stored)
        {
            try
            {
                return CertificateEnvelopes.ImportDeviceKey(Convert.FromBase64String(stored));
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                logger.LogWarning(ex, "Stored device encryption key unreadable; a new one needs approval");
            }
        }

        var key = CertificateEnvelopes.CreateDeviceKey();
        await credentials.SetSecretAsync(connection.EncryptionKeyId, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), cancellationToken);
        return key;
    }
}
