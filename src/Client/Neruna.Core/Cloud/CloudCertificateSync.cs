using System.Security.Cryptography;
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
/// key – remembered the first time – signed it. Certificates no longer assigned are removed (as with signatures).
/// </summary>
public sealed class CloudCertificateSync(
    CloudController cloud,
    CertificateManager certificates,
    ICertificateStore store,
    ICredentialStore credentials,
    ISettingsStore settings,
    ILogger<CloudCertificateSync> logger)
{
    public CloudCertificateStatus Status { get; private set; } = CloudCertificateStatus.None;

    public event EventHandler? Changed;

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (await cloud.GetConnectionAsync(cancellationToken) is not { } connection)
        {
            Status = CloudCertificateStatus.None;
            return await RemoveAllAsync(cancellationToken);
        }

        using var deviceKey = await DeviceKeyAsync(credentials, logger, cancellationToken);
        await cloud.SetDeviceEncryptionKeyAsync(CertificateEnvelopes.PublicKeyOf(deviceKey), cancellationToken);
        var response = await cloud.GetCertificatesAsync(cancellationToken);
        if (!response.Available)
        {
            Status = CloudCertificateStatus.None;
            return await RemoveAllAsync(cancellationToken);
        }

        // The organisation's signing key, trusted the first time it is seen.
        var signingKey = response.OrganizationKey?.SigningPublicKey;
        var pinned = await settings.GetAsync(SettingKeys.CloudOrganizationSigningKey, cancellationToken);
        if (signingKey is not null && pinned is null)
        {
            await settings.SetAsync(SettingKeys.CloudOrganizationSigningKey, signingKey, cancellationToken);
            pinned = signingKey;
        }

        if (signingKey is not null && signingKey != pinned)
        {
            logger.LogWarning("The organisation key for certificates changed; no certificates taken");
            Status = new CloudCertificateStatus(true, response.Device.Approved, 0, response.Certificates.Count,
                T("Der Organisationsschlüssel hat sich geändert – aus Sicherheitsgründen werden keine Zertifikate übernommen. Bitte die Cloud-Verbindung neu einrichten."));
            return false;
        }

        var stored = (await store.GetAllAsync(cancellationToken)).Where(s => s.Source == CertificateSource.Cloud).ToList();
        var present = stored.ToDictionary(s => Convert.ToHexStringLower(SHA256.HashData(s.Der)), StringComparer.Ordinal);
        var changed = false;
        var received = 0;
        var waiting = 0;
        foreach (var offered in response.Certificates)
        {
            if (present.Remove(offered.Fingerprint))
            {
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

        // No longer assigned (or deleted in the portal): gone here, too.
        foreach (var gone in present.Values)
        {
            await certificates.RemoveAsync(gone.Thumbprint, cancellationToken);
            changed = true;
        }

        Status = new CloudCertificateStatus(true, response.Device.Approved, received, waiting, null);
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>Disconnected: the organisation's certificates leave this device.</summary>
    public async Task<bool> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        var cloudCertificates = (await store.GetAllAsync(cancellationToken)).Where(s => s.Source == CertificateSource.Cloud).ToList();
        foreach (var certificate in cloudCertificates)
        {
            await certificates.RemoveAsync(certificate.Thumbprint, cancellationToken);
        }

        if (cloudCertificates.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return cloudCertificates.Count > 0;
    }

    // Made once per device; the private half only in the system keychain. Shared with the accounts from the cloud.
    internal static async Task<ECDiffieHellman> DeviceKeyAsync(ICredentialStore credentials, ILogger logger, CancellationToken cancellationToken)
    {
        if (await credentials.GetSecretAsync(CloudController.EncryptionKeyId, cancellationToken) is { Length: > 0 } stored)
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
        await credentials.SetSecretAsync(CloudController.EncryptionKeyId, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), cancellationToken);
        return key;
    }
}
