using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Neruna.Contracts.Cloud;
using Neruna.Core.Security;
using Neruna.Vault;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>What the desktop shows while a phone pairs: the QR code (invitation) and, separately, the PIN.</summary>
public sealed record PairingSession(string PairingId, PairingInvitation Invitation, string Pin, DateTimeOffset ExpiresAt);

/// <summary>
/// Pairs phones from this desktop and keeps them supplied, zero knowledge (contract: /device/pairings): the QR code
/// carries a secret the server never sees; the phone proves its encryption key with it, and only then does this desktop
/// re-encrypt the person's certificates and accounts for the phone – also those added later, on every sync.
/// </summary>
public sealed class CloudPairingService(CloudController cloud, ICredentialStore credentials, ISettingsStore settings, ILogger<CloudPairingService> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <exception cref="CloudException">Not connected, no phone allowed, device limit reached …</exception>
    public async Task<PairingSession> StartAsync(CancellationToken cancellationToken = default)
    {
        var connection = await cloud.GetConnectionAsync(cancellationToken)
                         ?? throw new CloudException("not_connected", T("Dieser Computer ist nicht mit Neruna Cloud verbunden."));
        var started = await cloud.StartPairingAsync(cancellationToken);
        var secret = PairingInvitation.NewSecret();
        var organizationKey = await settings.GetAsync(SettingKeys.CloudOrganizationSigningKey, cancellationToken);
        await credentials.SetSecretAsync(SecretId(started.PairingId), Convert.ToBase64String(secret), cancellationToken);
        return new PairingSession(
            started.PairingId,
            new PairingInvitation(connection.Server, started.Code, secret, organizationKey is null ? null : PairingInvitation.HashOfKey(organizationKey)),
            started.Pin,
            started.ExpiresAt);
    }

    /// <summary>Waits until the phone connected (it is returned) or the code expired (null).</summary>
    public async Task<PairedDevice?> WaitAsync(PairingSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        while (true)
        {
            var status = await cloud.GetPairingAsync(session.PairingId, cancellationToken);
            if (status is { Status: "connected", Device: { } phone })
            {
                // From now on the secret belongs to the phone (checking its key proof, now and after a new key).
                await credentials.SetSecretAsync(SecretId(phone.Id), Convert.ToBase64String(session.Invitation.Secret), cancellationToken);
                await credentials.DeleteSecretAsync(SecretId(session.PairingId), cancellationToken);
                return phone;
            }

            if (status.Status == "expired" || DateTimeOffset.Now > session.ExpiresAt)
            {
                await credentials.DeleteSecretAsync(SecretId(session.PairingId), cancellationToken);
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Re-encrypts for each phone of this desktop what it still misses – only for a key the phone proved with the secret
    /// from the QR code. Returns how many certificates and accounts were delivered.
    /// </summary>
    public async Task<int> DeliverAsync(CancellationToken cancellationToken = default)
    {
        if (await cloud.GetConnectionAsync(cancellationToken) is not { } connection)
        {
            return 0;
        }

        var phones = (await cloud.GetPairedDevicesAsync(cancellationToken)).Devices
            .Where(p => p.EncryptionPublicKey is not null && (!p.Approved || p.Missing.Certificates.Count + p.Missing.Accounts.Count > 0))
            .ToList();
        if (phones.Count == 0)
        {
            return 0;
        }

        CertificatesResponse? certificates = null;
        MailAccountsResponse? accounts = null;
        using var deviceKey = await CloudCertificateSync.DeviceKeyAsync(credentials, logger, cancellationToken);
        var delivered = 0;
        foreach (var phone in phones)
        {
            if (await credentials.GetSecretAsync(SecretId(phone.Id), cancellationToken) is not { } stored
                || !PairingInvitation.VerifyProof(Convert.FromBase64String(stored), phone.Id, phone.EncryptionPublicKey!, phone.PairingProof))
            {
                logger.LogWarning("Paired device {Device}: key not proven with the pairing secret; nothing delivered", phone.Id);
                continue;
            }

            if (phone.Missing.Certificates.Count > 0)
            {
                certificates ??= await cloud.GetCertificatesAsync(cancellationToken);
            }

            if (phone.Missing.Accounts.Count > 0)
            {
                accounts ??= await cloud.GetMailAccountsAsync(cancellationToken);
            }

            var certificateEnvelopes = Rewrap(phone, phone.Missing.Certificates,
                id => certificates?.Certificates.FirstOrDefault(c => c.Id == id)?.Envelope,
                (envelope, id) => CertificateEnvelopes.Unwrap(envelope, deviceKey, id, connection.DeviceId),
                id => CertificateEnvelopes.Context(id, phone.Id));
            var accountEnvelopes = Rewrap(phone, phone.Missing.Accounts,
                id => accounts?.Accounts.FirstOrDefault(a => a.Id == id)?.Envelope,
                (envelope, id) => AccountEnvelopes.Unwrap(envelope, deviceKey, id, connection.DeviceId),
                id => AccountEnvelopes.Context(id, phone.Id));

            // Nothing this desktop could open (it waits for approval itself): the phone is not marked as approved yet.
            if (certificateEnvelopes.Count + accountEnvelopes.Count == 0 && phone.Missing.Certificates.Count + phone.Missing.Accounts.Count > 0)
            {
                continue;
            }

            await cloud.DeliverToPairedDeviceAsync(phone.Id, new PairedEnvelopesRequest(certificateEnvelopes, accountEnvelopes), cancellationToken);
            delivered += certificateEnvelopes.Count + accountEnvelopes.Count;
            logger.LogInformation("Paired device {Device}: {Certificates} certificate(s) and {Accounts} account(s) delivered",
                phone.Id, certificateEnvelopes.Count, accountEnvelopes.Count);
        }

        return delivered;
    }

    private List<PairedEnvelope> Rewrap(
        PairedDevice phone,
        IReadOnlyList<string> ids,
        Func<string, string?> ownEnvelope,
        Func<string, string, byte[]> unwrap,
        Func<string, string> context)
    {
        var result = new List<PairedEnvelope>();
        foreach (var id in ids)
        {
            if (ownEnvelope(id) is not { } envelope)
            {
                continue; // not (yet) on this desktop
            }

            try
            {
                var key = unwrap(envelope, id);
                result.Add(new PairedEnvelope(id, CertificateEnvelopes.Wrap(key, phone.EncryptionPublicKey!, context(id))));
                CryptographicOperations.ZeroMemory(key);
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "Envelope {Id} could not be opened on this desktop; not delivered to {Device}", id, phone.Id);
            }
        }

        return result;
    }

    // Where a pairing secret is kept in the credential store (never in the database): per pairing, then per phone.
    private static Guid SecretId(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes("neruna-pairing|" + id)).AsSpan(0, 16));
}
