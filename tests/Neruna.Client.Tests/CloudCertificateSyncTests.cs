using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Core;
using Neruna.Core.Cloud;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>
/// Certificates from the organisation, end to end on the device side: the fixture was encrypted by the portal's browser
/// module for the fixture device; this device (with that key in its keychain) takes it, keeps it, and drops it again.
/// </summary>
public class CloudCertificateSyncTests
{
    private const string Server = "https://cloud.example/";

    [Fact]
    public async Task Certificates_of_the_organisation_arrive_stay_and_go()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = ContractFixtures.Read("certificates-example.json");
        var device = JsonDocument.Parse(ContractFixtures.Read("certificates-test-device.json")).RootElement;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var credentials = env.Get<ICredentialStore>();
        using var signingKey = DeviceKey.Create();
        await credentials.SetSecretAsync(CloudController.KeyId, signingKey.ExportPrivateKey(), ct);
        await credentials.SetSecretAsync(CloudController.EncryptionKeyId, device.GetProperty("privateKey").GetString()!, ct);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(
            new CloudConnection(new Uri(Server), device.GetProperty("deviceId").GetString()!, "Example AG", "Anna Muster", DateTimeOffset.Now), NerunaJson.Options), ct);
        env.Http.Responses[Server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        env.Http.Responses[Server + "api/v1/device/encryption-key"] = (HttpStatusCode.OK, """{"approved":true}""");
        env.Http.Responses[Server + "api/v1/certificates"] = (HttpStatusCode.OK, response);

        var cloud = new CloudController(new HttpClient(env.Http), settings, credentials, NullLogger<CloudController>.Instance);
        var manager = env.Get<CertificateManager>();
        var sync = new CloudCertificateSync(cloud, manager, env.Get<ICertificateStore>(), credentials, settings, NullLogger<CloudCertificateSync>.Instance);

        Assert.True(await sync.SyncAsync(ct));
        var own = Assert.Single(await manager.GetAllAsync(ct));
        Assert.True(own.HasPrivateKey);
        Assert.Equal(CertificateSource.Cloud, own.Source);
        Assert.Equal(["anna@example.com", "info@example.com"], own.EmailAddresses);
        Assert.Equal(new CloudCertificateStatus(true, true, 1, 0, null), sync.StatusOf(CloudConnection.FirstId));
        // The device sent its own key (public half) – the one from the keychain.
        var sent = env.Http.Sent.Single(s => s.Url.EndsWith("device/encryption-key", StringComparison.Ordinal));
        Assert.Equal(device.GetProperty("publicKey").GetString(), JsonDocument.Parse(sent.Body).RootElement.GetProperty("publicKey").GetString());

        // Already here: nothing to do; the organisation key is remembered.
        Assert.False(await sync.SyncAsync(ct));
        Assert.NotNull(await settings.GetAsync(SettingKeys.CloudOrganizationSigningKey, ct));

        // Another organisation key (e.g. a server slipping in its own): nothing taken, the user is told.
        var changed = JsonNode.Parse(response)!;
        changed["organizationKey"]!["signingPublicKey"] = changed["organizationKey"]!["encryptionPublicKey"]!.GetValue<string>();
        changed["certificates"] = new JsonArray();
        env.Http.Responses[Server + "api/v1/certificates"] = (HttpStatusCode.OK, changed.ToJsonString());
        Assert.False(await sync.SyncAsync(ct));
        Assert.NotNull(sync.StatusOf(CloudConnection.FirstId).Problem);
        Assert.Single(await manager.GetAllAsync(ct));

        // No longer assigned: removed here, too.
        var none = JsonNode.Parse(response)!;
        none["certificates"] = new JsonArray();
        env.Http.Responses[Server + "api/v1/certificates"] = (HttpStatusCode.OK, none.ToJsonString());
        Assert.True(await sync.SyncAsync(ct));
        Assert.Empty(await manager.GetAllAsync(ct));
    }

    [Fact]
    public async Task Pgp_keys_and_the_directory_of_the_organisation_arrive_are_trusted_and_go()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = ContractFixtures.Read("pgp-keys-example.json");
        var device = JsonDocument.Parse(ContractFixtures.Read("certificates-test-device.json")).RootElement;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var credentials = env.Get<ICredentialStore>();
        using var signingKey = DeviceKey.Create();
        await credentials.SetSecretAsync(CloudController.KeyId, signingKey.ExportPrivateKey(), ct);
        await credentials.SetSecretAsync(CloudController.EncryptionKeyId, device.GetProperty("privateKey").GetString()!, ct);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(
            new CloudConnection(new Uri(Server), device.GetProperty("deviceId").GetString()!, "Example AG", "Anna Muster", DateTimeOffset.Now), NerunaJson.Options), ct);
        env.Http.Responses[Server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        env.Http.Responses[Server + "api/v1/device/encryption-key"] = (HttpStatusCode.OK, """{"approved":true}""");
        // No certificates – the same organisation key as the PGP keys.
        var certificates = JsonNode.Parse(ContractFixtures.Read("certificates-example.json"))!;
        certificates["certificates"] = new JsonArray();
        env.Http.Responses[Server + "api/v1/certificates"] = (HttpStatusCode.OK, certificates.ToJsonString());

        var cloud = new CloudController(new HttpClient(env.Http), settings, credentials, NullLogger<CloudController>.Instance);
        var pgp = env.Get<PgpKeyManager>();
        var sync = new CloudCertificateSync(cloud, env.Get<CertificateManager>(), env.Get<ICertificateStore>(), credentials, settings,
            NullLogger<CloudCertificateSync>.Instance, pgp);

        // An older server (404): nothing, no error.
        Assert.False(await sync.SyncAsync(ct));
        Assert.Empty(await pgp.GetAllAsync(ct));

        // Bruno's key was seen in a mail before (known, not confirmed): the directory makes it the organisation's.
        var fixture = JsonNode.Parse(response)!;
        var brunoRing = PgpKeyParser.Read(Convert.FromBase64String(fixture["directory"]![1]!["publicKey"]!.GetValue<string>())).Public[0];
        await pgp.RememberAsync(brunoRing, PgpKeySource.Autocrypt, ct);

        env.Http.Responses[Server + "api/v1/pgp-keys"] = (HttpStatusCode.OK, response);
        Assert.True(await sync.SyncAsync(ct));
        var keys = await pgp.GetAllAsync(ct);
        Assert.Equal(2, keys.Count);
        var own = Assert.Single(keys, k => k.HasSecretKey);
        Assert.Equal(PgpKeySource.Cloud, own.Source);
        Assert.Equal(["anna@example.com"], own.EmailAddresses);
        Assert.True(own.CanSign);
        var bruno = Assert.Single(keys, k => !k.HasSecretKey);
        Assert.Equal(PgpKeySource.Organization, bruno.Source);
        Assert.True(bruno.IsTrusted);
        Assert.Equal(new CloudCertificateStatus(true, true, 1, 0, null), sync.StatusOf(CloudConnection.FirstId));
        // The own key works: it signs (its passphrase came along into the keychain).
        Assert.Single((await pgp.LoadKeyringAsync(ct)).Secret);

        Assert.False(await sync.SyncAsync(ct));

        // A directory entry the organisation did not sign is not taken.
        var forged = JsonNode.Parse(response)!;
        forged["directory"]![1]!["publicSignature"] = forged["directory"]![0]!["publicSignature"]!.GetValue<string>();
        forged["keys"] = new JsonArray();
        env.Http.Responses[Server + "api/v1/pgp-keys"] = (HttpStatusCode.OK, forged.ToJsonString());
        Assert.True(await sync.SyncAsync(ct));
        // Anna's key no longer assigned to her: the secret part goes, the public key stays (it is in the directory).
        var left = Assert.Single(await pgp.GetAllAsync(ct));
        Assert.Equal((PgpKeySource.Organization, false), (left.Source, left.HasSecretKey));

        // No longer listed at all: gone.
        var none = JsonNode.Parse(response)!;
        none["keys"] = new JsonArray();
        none["directory"] = new JsonArray();
        env.Http.Responses[Server + "api/v1/pgp-keys"] = (HttpStatusCode.OK, none.ToJsonString());
        Assert.True(await sync.SyncAsync(ct));
        Assert.Empty(await pgp.GetAllAsync(ct));
    }
}
