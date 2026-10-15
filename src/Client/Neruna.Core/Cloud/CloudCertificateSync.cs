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
/// Brings the S/MIME certificates the organisation assigned to this person onto this device, zero knowledge: this
/// device's encryption key (in the keychain) opens its envelope; a certificate is taken only when the organisation
/// key – remembered the first time – signed it. Certificates no longer assigned are removed (as with signatures). Each
/// connected organisation on its own: own keys, own signing key, own list of what it delivered.
/// </summary>
public sealed class CloudCertificateSync(
    CloudController cloud,
    CertificateManager certificates,
    ICertificateStore store,
    ICredentialStore credentials,
    ISettingsStore settings,
    ILogger<CloudCertificateSync> logger)
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
    /// Disconnected (or no longer assigned): the certificates no connected organisation has given this device leave it.
    /// </summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
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

        if (gone.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return gone.Count > 0;
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
