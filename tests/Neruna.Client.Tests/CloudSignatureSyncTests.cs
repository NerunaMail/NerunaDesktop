using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Core;
using Neruna.Core.Cloud;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public class CloudSignatureSyncTests
{
    private const string Server = "https://cloud.example/";

    private static string Signatures(params (string Id, string Name, string Html)[] list) => JsonSerializer.Serialize(new
    {
        available = true,
        signatures = list.Select(s => new { id = s.Id, name = s.Name, html = s.Html, text = s.Name, updatedAt = DateTimeOffset.Now }),
    });

    [Fact]
    public async Task Central_signatures_are_added_updated_and_removed_without_touching_own_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var store = env.Get<ISignatureStore>();
        var service = new SignatureService(store, settings);
        using var key = DeviceKey.Create();
        await env.Get<ICredentialStore>().SetSecretAsync(CloudController.KeyId, key.ExportPrivateKey(), ct);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(
            new CloudConnection(new Uri(Server), "01DEVICE", "Example AG", "Anna Muster", DateTimeOffset.Now), NerunaJson.Options), ct);
        env.Http.Responses[Server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        env.Http.Responses[Server + "api/v1/signatures"] = (HttpStatusCode.OK, Signatures(("A", "Example AG", "<p>Anna</p>"), ("B", "Example AG intern", "<p>Gruss</p>")));

        var own = new Signature(Guid.NewGuid(), "Meine", "<p>privat</p>", DateTimeOffset.Now);
        await service.SaveAsync(own, ct);
        var cloud = new CloudController(new HttpClient(env.Http), settings, env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);
        var sync = new CloudSignatureSync(cloud, service, NullLogger<CloudSignatureSync>.Instance);
        var changes = 0;
        sync.Changed += (_, _) => changes++;

        Assert.True(await sync.SyncAsync(ct));
        var all = await service.GetAllAsync(ct);
        Assert.Equal(["Example AG", "Example AG intern", "Meine"], all.Select(s => s.Name));
        Assert.All(all.Where(s => s.Name != "Meine"), s => Assert.True(s.IsFromCloud));
        var firstId = all.Single(s => s.Name == "Example AG").Id;
        Assert.Equal(CloudSignatureSync.LocalId("A"), firstId);

        // A per-account choice survives updates (same local id).
        var account = Guid.NewGuid();
        await service.SetAssignmentAsync(account, new SignatureAssignment(firstId, null), ct);

        // Unchanged: nothing to do.
        Assert.False(await sync.SyncAsync(ct));

        // Changed in the portal, one removed there.
        env.Http.Responses[Server + "api/v1/signatures"] = (HttpStatusCode.OK, Signatures(("A", "Example AG", "<p>Anna Muster</p>")));
        Assert.True(await sync.SyncAsync(ct));
        all = await service.GetAllAsync(ct);
        Assert.Equal(["Example AG", "Meine"], all.Select(s => s.Name));
        Assert.Equal("<p>Anna Muster</p>", (await service.ResolveAsync(account, ComposeKind.New, ct))!.Html);

        // Disconnected: the organisation's signatures go, the own one stays.
        Assert.True(await sync.RemoveAllAsync(ct));
        Assert.Equal(["Meine"], (await service.GetAllAsync(ct)).Select(s => s.Name));
        Assert.Equal(3, changes);
    }
}
