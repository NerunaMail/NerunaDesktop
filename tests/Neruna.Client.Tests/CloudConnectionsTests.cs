using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core;
using Neruna.Core.Cloud;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>One computer connected to two organisations: each a device of its own, its things apart from the other's.</summary>
public class CloudConnectionsTests
{
    private const string First = "https://cloud.example/";
    private const string Second = "https://neruna.partner.example/";

    private static void Organisation(TestEnvironment env, string server, string organisation, string device, params (string Id, string Name)[] signatures)
    {
        env.Http.Responses[server + "api/v1/enrollment"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new { deviceId = device, organizationName = organisation, memberName = "Anna Muster" }));
        env.Http.Responses[server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        env.Http.Responses[server + "api/v1/device"] = (HttpStatusCode.NoContent, string.Empty);
        env.Http.Responses[server + "api/v1/signatures"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            available = true,
            signatures = signatures.Select(s => new { id = s.Id, name = s.Name, html = $"<p>{s.Name}</p>", text = s.Name, updatedAt = DateTimeOffset.Now }),
        }));
    }

    [Fact]
    public async Task Two_organisations_each_bring_their_own_and_take_it_along_when_disconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var credentials = env.Get<ICredentialStore>();
        Organisation(env, First, "Example AG", "DEV-A", ("A", "Example AG"));
        Organisation(env, Second, "Partner GmbH", "DEV-B", ("A", "Partner GmbH")); // the same id on another server
        var cloud = new CloudController(new HttpClient(env.Http), settings, credentials, NullLogger<CloudController>.Instance);
        var device = new DeviceInfo("Testgerät", "0.8.0", "Linux", "Debian 13", "anna");

        // The first keeps the places of single-connection versions; the second gets its own.
        var example = await cloud.ConnectAsync(new Uri(First), "CODE", "123456", device, ct);
        var partner = await cloud.ConnectAsync(new Uri(Second), "CODE", "123456", device, ct);
        Assert.True(example.IsFirst);
        Assert.False(partner.IsFirst);
        Assert.NotNull(await settings.GetAsync(SettingKeys.CloudConnection, ct));
        Assert.NotNull(await credentials.GetSecretAsync(CloudController.KeyId, ct));
        Assert.NotNull(await credentials.GetSecretAsync(partner.DeviceKeyId, ct));
        Assert.NotEqual(example.DeviceKeyId, partner.DeviceKeyId);
        Assert.NotEqual(example.EncryptionKeyId, partner.EncryptionKeyId);
        Assert.Equal(["Example AG", "Partner GmbH"], (await cloud.GetConnectionsAsync(ct)).Select(c => c.OrganizationName));

        // Chat and backups: the first, until another is chosen.
        Assert.Equal(example.Id, (await cloud.GetChatConnectionAsync(ct))!.Id);
        await cloud.SetChatConnectionAsync(partner.Id, ct);
        Assert.Equal(partner.Id, (await cloud.GetChatConnectionAsync(ct))!.Id);
        Assert.Equal(example.Id, (await cloud.GetBackupConnectionAsync(ct))!.Id);

        // Each organisation's signatures, apart even with the same id.
        var signatures = new SignatureService(env.Get<ISignatureStore>(), settings);
        var sync = new CloudSignatureSync(cloud, signatures, NullLogger<CloudSignatureSync>.Instance);
        Assert.True(await sync.SyncAsync(ct));
        var all = await signatures.GetAllAsync(ct);
        Assert.Equal(["Example AG", "Partner GmbH"], all.Select(s => s.Name));
        Assert.Equal(CloudSignatureSync.LocalId("A"), all[0].Id); // as before for the first
        Assert.Equal(partner.Source, all[1].Source);
        Assert.All(all, s => Assert.True(s.IsFromCloud));

        // One unreachable: the other is still synced, the error comes afterwards.
        env.Http.Responses[Second + "api/v1/signatures"] = (HttpStatusCode.OK, """{"available":true,"signatures":[]}""");
        env.Http.Responses[First + "api/v1/signatures"] = (HttpStatusCode.BadGateway, string.Empty);
        await Assert.ThrowsAsync<CloudException>(() => sync.SyncAsync(ct));
        Assert.Equal(["Example AG"], (await signatures.GetAllAsync(ct)).Select(s => s.Name));
        env.Http.Responses[Second + "api/v1/signatures"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            available = true,
            signatures = new[] { new { id = "A", name = "Partner GmbH", html = "<p>Partner</p>", text = "Partner", updatedAt = DateTimeOffset.Now } },
        }));
        env.Http.Responses[First + "api/v1/signatures"] = (HttpStatusCode.OK, """{"available":true,"signatures":[{"id":"A","name":"Example AG","html":"<p>Example AG</p>","text":"x","updatedAt":"2026-10-15T10:00:00Z"}]}""");

        // Connected again to the same organisation: it takes the old one's place, keeps its things.
        var again = await cloud.ConnectAsync(new Uri(Second), "CODE2", "654321", device, ct);
        Assert.Equal(partner.Id, again.Id);
        Assert.Equal(2, (await cloud.GetConnectionsAsync(ct)).Count);

        // Disconnecting the first: only its things go; the chat stays with the partner, the backups move to it.
        await sync.SyncAsync(ct);
        await cloud.DisconnectAsync(example, ct);
        Assert.Null(await settings.GetAsync(SettingKeys.CloudConnection, ct));
        Assert.Null(await credentials.GetSecretAsync(CloudController.KeyId, ct));
        Assert.True(await sync.RemoveDisconnectedAsync(ct));
        Assert.Equal(["Partner GmbH"], (await signatures.GetAllAsync(ct)).Select(s => s.Name));
        Assert.Equal(partner.Id, (await cloud.GetBackupConnectionAsync(ct))!.Id);

        // A further connection now does not take the first's places (another one is still there).
        Organisation(env, First, "Example AG", "DEV-C");
        var back = await cloud.ConnectAsync(new Uri(First), "CODE3", "111111", device, ct);
        Assert.False(back.IsFirst);
    }

    [Fact]
    public async Task An_organisation_that_blocks_this_device_takes_everything_it_brought_along()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new StubHttpHandler();
        await using var env = await TestEnvironment.CreateAsync(services =>
        {
            services.AddSingleton(sp => new CloudController(new HttpClient(http), sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<ICredentialStore>(), NullLogger<CloudController>.Instance));
            services.AddSingleton<TextTemplateService>();
            services.AddSingleton<CloudSignatureSync>();
            services.AddSingleton<CloudTextTemplateSync>();
            services.AddSingleton<MailTagService>();
            services.AddSingleton<CloudMailTagSync>();
            services.AddSingleton<CloudCertificateSync>();
            services.AddSingleton<CloudAccountSync>();
            services.AddSingleton<SettingsBackupService>();
            services.AddSingleton<CloudRevocation>();
        });
        var settings = env.Get<ISettingsStore>();
        var credentials = env.Get<ICredentialStore>();
        var cloud = env.Get<CloudController>();
        env.Get<CloudRevocation>();
        var revoked = new List<string>();
        cloud.Revoked += (_, c) => revoked.Add(c.OrganizationName);
        foreach (var (server, organisation, deviceId) in new[] { (First, "Example AG", "DEV-A"), (Second, "Partner GmbH", "DEV-B") })
        {
            http.Responses[server + "api/v1/enrollment"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new { deviceId, organizationName = organisation, memberName = "Anna Muster" }));
            http.Responses[server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
            http.Responses[server + "api/v1/signatures"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                available = true,
                signatures = new[] { new { id = "A", name = organisation, html = $"<p>{organisation}</p>", text = organisation, updatedAt = DateTimeOffset.Now } },
            }));
        }

        // Tags: the first server is older (no tags yet), the partner has one.
        http.Responses[First + "api/v1/mail-tags"] = (HttpStatusCode.NotFound, """{"message":"Not found"}""");
        http.Responses[Second + "api/v1/mail-tags"] = (HttpStatusCode.OK, """{"available":true,"tags":[{"id":"T","name":"Offerte","keyword":"offerte","color":"#e8590c","updatedAt":"2026-10-27T12:00:00+01:00"}]}""");

        var device = new DeviceInfo("Testgerät", "0.8.2", "Linux", "Debian 13", "anna");
        var example = await cloud.ConnectAsync(new Uri(First), "CODE", "123456", device, ct);
        var partner = await cloud.ConnectAsync(new Uri(Second), "CODE", "123456", device, ct);
        await credentials.SetSecretAsync(partner.VaultKeyId, "vault:key", ct);
        var signatures = env.Get<SignatureService>();
        var sync = env.Get<CloudSignatureSync>();
        await sync.SyncAsync(ct);
        await env.Get<ISignatureStore>().SaveAsync(new Signature(Guid.NewGuid(), "Eigene", "<p>Anna</p>", DateTimeOffset.UnixEpoch), ct);
        Assert.Equal(3, (await signatures.GetAllAsync(ct)).Count);
        await env.Get<CloudMailTagSync>().SyncAsync(ct);
        var tags = env.Get<MailTagService>();
        Assert.Equal(["offerte", "$label1"], (await tags.GetAllAsync(ct)).Take(2).Select(t => t.Keyword));

        // Blocked in the partner's portal: its tokens are void, a new one is refused.
        http.Responses[Second + "api/v1/signatures"] = (HttpStatusCode.Unauthorized, """{"error":"unauthenticated","message":"Token ungültig"}""");
        http.Responses[Second + "api/v1/token"] = (HttpStatusCode.Unauthorized, """{"error":"device_revoked","message":"Gerät gesperrt"}""");
        var refused = await Assert.ThrowsAsync<CloudException>(() => sync.SyncAsync(ct));
        Assert.Equal(CloudController.RevokedCode, refused.Code);
        Assert.Contains("Partner GmbH", refused.Message, StringComparison.Ordinal);

        // Partner's connection, keys and vault key are gone, and its signature; the own one and Example's stay.
        Assert.Equal(["Partner GmbH"], revoked);
        Assert.Equal([example.Id], (await cloud.GetConnectionsAsync(ct)).Select(c => c.Id));
        Assert.Null(await credentials.GetSecretAsync(partner.DeviceKeyId, ct));
        Assert.Null(await credentials.GetSecretAsync(partner.EncryptionKeyId, ct));
        Assert.Null(await credentials.GetSecretAsync(partner.VaultKeyId, ct));
        Assert.Equal(["Eigene", "Example AG"], (await signatures.GetAllAsync(ct)).Select(s => s.Name).Order());
        Assert.DoesNotContain(await tags.GetAllAsync(ct), t => t.IsFromCloud);

        // Any other refusal (wrong key, server error) removes nothing.
        http.Responses[First + "api/v1/signatures"] = (HttpStatusCode.Unauthorized, """{"error":"unauthenticated","message":"Token ungültig"}""");
        http.Responses[First + "api/v1/token"] = (HttpStatusCode.Unauthorized, """{"error":"invalid_assertion","message":"Signatur ungültig"}""");
        await Assert.ThrowsAsync<CloudException>(() => sync.SyncAsync(ct));
        Assert.Single(await cloud.GetConnectionsAsync(ct));
        Assert.Equal(["Partner GmbH"], revoked);
    }

    [Fact]
    public void Local_ids_say_which_organisation_they_belong_to()
    {
        var first = new CloudConnection(new Uri(First), "D1", "Example AG", "Anna", DateTimeOffset.Now);
        var other = first with { Id = "b7" };

        Assert.Equal("01ACC", first.LocalId("01ACC"));
        Assert.Equal("b7:01ACC", other.LocalId("01ACC"));
        Assert.True(first.Owns("01ACC"));
        Assert.False(first.Owns("b7:01ACC"));
        Assert.True(other.Owns("b7:01ACC"));
        Assert.False(other.Owns("01ACC"));
        Assert.Equal(("cloud", "cloud:b7"), (first.Source, other.Source));
        Assert.Equal(("cloud.accounts", "cloud.accounts.b7"), (first.SettingKey("cloud.accounts"), other.SettingKey("cloud.accounts")));
        Assert.True(CloudSources.IsCloud("cloud:b7"));
        Assert.False(CloudSources.IsCloud("local"));
    }
}
