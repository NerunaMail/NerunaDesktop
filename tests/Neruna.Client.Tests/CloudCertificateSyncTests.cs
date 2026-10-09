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
        Assert.Equal(new CloudCertificateStatus(true, true, 1, 0, null), sync.Status);
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
        Assert.NotNull(sync.Status.Problem);
        Assert.Single(await manager.GetAllAsync(ct));

        // No longer assigned: removed here, too.
        var none = JsonNode.Parse(response)!;
        none["certificates"] = new JsonArray();
        env.Http.Responses[Server + "api/v1/certificates"] = (HttpStatusCode.OK, none.ToJsonString());
        Assert.True(await sync.SyncAsync(ct));
        Assert.Empty(await manager.GetAllAsync(ct));
    }
}
