using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Vault;

namespace Neruna.Shared.Tests;

/// <summary>
/// The contract fixtures were encrypted by the portal's browser module (server/public/js/neruna-certificates.js, run
/// in Node): here the app has to open them – the same format on both sides.
/// </summary>
public class CertificateEnvelopeTests
{
    private static readonly JsonElement Device = JsonDocument.Parse(ContractFixtures.Read("certificates-test-device.json")).RootElement;

    private static CertificatesResponse Response() =>
        JsonSerializer.Deserialize<CertificatesResponse>(ContractFixtures.Read("certificates-example.json"), NerunaJson.Options)!;

    [Fact]
    public void A_device_opens_what_the_portal_encrypted_for_it()
    {
        var response = Response();
        var certificate = Assert.Single(response.Certificates);
        Assert.True(CertificateEnvelopes.Verify(certificate.Id, certificate.Payload, certificate.PayloadSignature, response.OrganizationKey!.SigningPublicKey));

        using var deviceKey = CertificateEnvelopes.ImportDeviceKey(Convert.FromBase64String(Device.GetProperty("privateKey").GetString()!));
        Assert.Equal(Device.GetProperty("publicKey").GetString(), CertificateEnvelopes.PublicKeyOf(deviceKey));
        var key = CertificateEnvelopes.Unwrap(certificate.Envelope!, deviceKey, certificate.Id, Device.GetProperty("deviceId").GetString()!);
        var (pkcs12, password) = CertificateEnvelopes.Decrypt(certificate.Id, certificate.Payload, key);

        Assert.Equal("geheim", password);
        using var opened = X509CertificateLoader.LoadPkcs12(pkcs12, password);
        Assert.True(opened.HasPrivateKey);
        Assert.Equal(certificate.Fingerprint, Convert.ToHexStringLower(SHA256.HashData(opened.RawData)));
        Assert.Equal(["anna@example.com", "info@example.com"], certificate.EmailAddresses);
    }

    [Fact]
    public void Changed_or_misdirected_data_is_refused()
    {
        var response = Response();
        var certificate = response.Certificates[0];
        var signing = response.OrganizationKey!.SigningPublicKey;

        // A payload the organisation did not sign (e.g. slipped in by a server) is not taken.
        Assert.False(CertificateEnvelopes.Verify(certificate.Id, certificate.Payload.Replace("\"iv\":\"", "\"iv\":\"A", StringComparison.Ordinal), certificate.PayloadSignature, signing));
        Assert.False(CertificateEnvelopes.Verify("01OTHER", certificate.Payload, certificate.PayloadSignature, signing));
        Assert.False(CertificateEnvelopes.Verify(certificate.Id, certificate.Payload, certificate.PayloadSignature, response.OrganizationKey.EncryptionPublicKey));

        // An envelope opens only on its device, for its certificate.
        using var deviceKey = CertificateEnvelopes.ImportDeviceKey(Convert.FromBase64String(Device.GetProperty("privateKey").GetString()!));
        Assert.ThrowsAny<CryptographicException>(() => CertificateEnvelopes.Unwrap(certificate.Envelope!, deviceKey, certificate.Id, "01OTHERDEVICE"));
        using var otherDevice = CertificateEnvelopes.CreateDeviceKey();
        Assert.ThrowsAny<CryptographicException>(() => CertificateEnvelopes.Unwrap(certificate.Envelope!, otherDevice, certificate.Id, Device.GetProperty("deviceId").GetString()!));
    }
}

/// <summary>PGP keys from the portal (same envelopes as certificates) and the organisation's signed directory.</summary>
public class PgpKeyEnvelopeTests
{
    private static readonly JsonElement Device = JsonDocument.Parse(ContractFixtures.Read("certificates-test-device.json")).RootElement;

    private static PgpKeysResponse Response() =>
        JsonSerializer.Deserialize<PgpKeysResponse>(ContractFixtures.Read("pgp-keys-example.json"), NerunaJson.Options)!;

    [Fact]
    public void A_device_opens_its_pgp_key_and_trusts_only_what_the_organisation_signed()
    {
        var response = Response();
        var signing = response.OrganizationKey!.SigningPublicKey;
        var key = Assert.Single(response.Keys);
        Assert.True(CertificateEnvelopes.Verify(key.Id, key.Payload, key.PayloadSignature, signing));

        using var deviceKey = CertificateEnvelopes.ImportDeviceKey(Convert.FromBase64String(Device.GetProperty("privateKey").GetString()!));
        var wrapped = CertificateEnvelopes.Unwrap(key.Envelope!, deviceKey, key.Id, Device.GetProperty("deviceId").GetString()!);
        var (secretKey, passphrase) = CertificateEnvelopes.DecryptPgp(key.Id, key.Payload, wrapped);
        Assert.Equal(0xC5, secretKey[0]); // a secret key packet
        Assert.False(string.IsNullOrEmpty(passphrase));
        Assert.Matches("^[0-9a-f]{40}$", key.Fingerprint);

        Assert.Equal(2, response.Directory.Count);
        foreach (var entry in response.Directory)
        {
            Assert.True(CertificateEnvelopes.VerifyPgpPublicKey(entry.Id, entry.PublicKey, entry.PublicSignature, signing));
            Assert.False(CertificateEnvelopes.VerifyPgpPublicKey("01OTHER", entry.PublicKey, entry.PublicSignature, signing));
        }

        Assert.False(CertificateEnvelopes.VerifyPgpPublicKey(response.Directory[0].Id, response.Directory[1].PublicKey, response.Directory[0].PublicSignature, signing));
        // A certificate payload is no PGP key.
        var certificates = JsonSerializer.Deserialize<CertificatesResponse>(ContractFixtures.Read("certificates-example.json"), NerunaJson.Options)!;
        var certificate = certificates.Certificates[0];
        var certificateKey = CertificateEnvelopes.Unwrap(certificate.Envelope!, deviceKey, certificate.Id, Device.GetProperty("deviceId").GetString()!);
        Assert.ThrowsAny<CryptographicException>(() => CertificateEnvelopes.DecryptPgp(certificate.Id, certificate.Payload, certificateKey));
    }
}
