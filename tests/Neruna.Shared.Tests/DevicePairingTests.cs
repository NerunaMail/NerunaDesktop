using System.Security.Cryptography;
using System.Text.Json;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Vault;

namespace Neruna.Shared.Tests;

/// <summary>Pairing a phone from a desktop: the QR code, the phone's key proof, and the desktop re-encrypting for the phone.</summary>
public class DevicePairingTests
{
    private static readonly JsonElement Device = JsonDocument.Parse(ContractFixtures.Read("certificates-test-device.json")).RootElement;

    [Fact]
    public void The_invitation_survives_the_qr_code_and_rejects_other_codes()
    {
        var invitation = new PairingInvitation(new Uri("https://neruna.cloud/"), "ABCD-EFGH-JKMN-PQRS", PairingInvitation.NewSecret(), PairingInvitation.HashOfKey(Convert.ToBase64String(new byte[91])));

        var parsed = PairingInvitation.Parse(invitation.ToString());

        Assert.Equal(invitation.Server, parsed.Server);
        Assert.Equal(invitation.Code, parsed.Code);
        Assert.Equal(invitation.Secret, parsed.Secret);
        Assert.Equal(invitation.OrganizationKeyHash, parsed.OrganizationKeyHash);
        Assert.Null(PairingInvitation.Parse(invitation with { OrganizationKeyHash = null } + string.Empty).OrganizationKeyHash);
        Assert.Throws<FormatException>(() => PairingInvitation.Parse("https://example.com/?code=1"));
        Assert.Throws<FormatException>(() => PairingInvitation.Parse("neruna-pair:1?server=https%3A%2F%2Fx%2F&code=A&secret=c2hvcnQ"));
        Assert.Throws<FormatException>(() => PairingInvitation.Parse("neruna-pair:1?server=file%3A%2F%2F%2Fetc&code=A&secret=" + new string('A', 43)));
    }

    [Fact]
    public void Only_the_holder_of_the_secret_proves_a_key()
    {
        var secret = PairingInvitation.NewSecret();
        var proof = PairingInvitation.Proof(secret, "01PHONE", "KEY");

        Assert.True(PairingInvitation.VerifyProof(secret, "01PHONE", "KEY", proof));
        Assert.False(PairingInvitation.VerifyProof(secret, "01PHONE", "OTHER-KEY", proof));
        Assert.False(PairingInvitation.VerifyProof(secret, "01OTHER", "KEY", proof));
        Assert.False(PairingInvitation.VerifyProof(PairingInvitation.NewSecret(), "01PHONE", "KEY", proof));
        Assert.False(PairingInvitation.VerifyProof(secret, "01PHONE", "KEY", null));
        Assert.False(PairingInvitation.VerifyProof(secret, "01PHONE", "KEY", "not base64!"));
    }

    [Fact]
    public void A_desktop_rewraps_a_certificate_key_the_phone_can_open()
    {
        var response = JsonSerializer.Deserialize<CertificatesResponse>(ContractFixtures.Read("certificates-example.json"), NerunaJson.Options)!;
        var certificate = Assert.Single(response.Certificates);
        using var desktopKey = CertificateEnvelopes.ImportDeviceKey(Convert.FromBase64String(Device.GetProperty("privateKey").GetString()!));
        var key = CertificateEnvelopes.Unwrap(certificate.Envelope!, desktopKey, certificate.Id, Device.GetProperty("deviceId").GetString()!);

        using var phoneKey = CertificateEnvelopes.CreateDeviceKey();
        var envelope = CertificateEnvelopes.Wrap(key, CertificateEnvelopes.PublicKeyOf(phoneKey), CertificateEnvelopes.Context(certificate.Id, "01PHONE"));

        var opened = CertificateEnvelopes.Unwrap(envelope, phoneKey, certificate.Id, "01PHONE");
        Assert.Equal(key, opened);
        Assert.Equal("geheim", CertificateEnvelopes.Decrypt(certificate.Id, certificate.Payload, opened).Password);
        // Bound to the phone and the certificate: another device id or an account context does not open it.
        Assert.ThrowsAny<CryptographicException>(() => CertificateEnvelopes.Unwrap(envelope, phoneKey, certificate.Id, "01OTHER"));
        Assert.ThrowsAny<CryptographicException>(() => AccountEnvelopes.Unwrap(envelope, phoneKey, certificate.Id, "01PHONE"));
    }
}
