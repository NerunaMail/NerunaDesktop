using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>Accounts the organisation set up in the portal (contract/fixtures, made by the browser module).</summary>
public class CloudAccountSyncTests
{
    private const string Server = "https://cloud.example/";

    [Fact]
    public async Task Accounts_of_the_organisation_arrive_ask_for_a_missing_password_and_go()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = ContractFixtures.Read("mail-accounts-example.json");
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
        env.Http.Responses[Server + "api/v1/mail-accounts"] = (HttpStatusCode.OK, response);

        var cloud = new CloudController(new HttpClient(env.Http), settings, credentials, NullLogger<CloudController>.Instance);
        var registry = new ProviderRegistry(
            [new ImapProviderFactory(credentials, NullLoggerFactory.Instance)],
            [new CalDavProviderFactory(new HttpClient(env.Http), credentials, NullLoggerFactory.Instance)],
            []);
        var setup = new AccountSetupService(registry, env.Accounts, credentials);
        var sync = new CloudAccountSync(cloud, env.Accounts, setup, credentials, settings, NullLogger<CloudAccountSync>.Instance);

        // Anna's account came with its password: set up at once, read-only servers from the organisation.
        Assert.True(await sync.SyncAsync(ct));
        var anna = Assert.Single(await env.Accounts.GetAccountsAsync(ct));
        Assert.Equal(("01K7ACCT000000000000000000", "anna@example.com", "Anna Muster"), (anna.CloudId, anna.EmailAddress, anna.DisplayName));
        Assert.Equal(["anna@example.com", "anna.muster@example.com"], anna.Identities.Select(i => i.Email));
        Assert.Equal([ServiceKind.Mail, ServiceKind.Calendar], anna.Connections.Select(c => c.Kind).Order());
        Assert.Equal("mail.example.com", anna.ConnectionsOf(ServiceKind.Mail).Single().Settings["imap.host"]);
        Assert.All(anna.Connections, c => Assert.Equal("geheim", credentials.GetSecretAsync(c.Id, ct).Result));

        // info@ came without one: the user is asked; nothing is set up yet.
        var pending = Assert.Single(sync.Status.Pending);
        Assert.Equal(("info@example.com", "Example AG"), (pending.Email, pending.DisplayName));

        // Unchanged: nothing to do. A label of the user's stays when the organisation changes the account.
        Assert.False(await sync.SyncAsync(ct));
        await env.Accounts.SaveAccountAsync(anna with { Label = "Büro" }, ct);
        await settings.SetAsync("cloud.accounts", "{}", ct);
        await sync.SyncAsync(ct);
        var again = Assert.Single(await env.Accounts.GetAccountsAsync(ct));
        Assert.Equal((anna.Id, "Büro", anna.Connections[0].Id), (again.Id, again.Label, again.Connections[0].Id));

        // A payload changed by the server (not signed by the organisation): not taken.
        var forged = JsonNode.Parse(response)!;
        forged["accounts"]![0]!["payloadSignature"] = forged["accounts"]![1]!["payloadSignature"]!.GetValue<string>();
        await settings.SetAsync("cloud.accounts", "{}", ct);
        env.Http.Responses[Server + "api/v1/mail-accounts"] = (HttpStatusCode.OK, forged.ToJsonString());
        await sync.SyncAsync(ct);
        Assert.Equal(1, sync.Status.Waiting);

        // With the password the user typed, the next sync sets info@ up too.
        await CloudAccountPasswords.SetAsync(credentials, env.Accounts, pending.CloudId, "info-pw", ct);
        env.Http.Responses[Server + "api/v1/mail-accounts"] = (HttpStatusCode.OK, response);
        await sync.SyncAsync(ct);
        Assert.Empty(sync.Status.Pending);
        Assert.Equal(2, (await env.Accounts.GetAccountsAsync(ct)).Count);

        // That typed password goes into the personal backup; the organisation's accounts themselves do not.
        var backup = new SettingsBackupService(cloud, env.Accounts, credentials, settings, env.Get<Neruna.Core.Mail.ISignatureStore>(), env.Get<Neruna.Core.Mail.ITextTemplateStore>(),
            env.Get<CertificateManager>(), TimeProvider.System, NullLogger<SettingsBackupService>.Instance);
        var content = await backup.CollectAsync(ct);
        Assert.Empty(content.Accounts);
        Assert.Equal("info-pw", Assert.Single(content.CloudPasswords!).Value);

        // No longer assigned: removed with its passwords.
        var none = JsonNode.Parse(response)!;
        none["accounts"] = new JsonArray();
        env.Http.Responses[Server + "api/v1/mail-accounts"] = (HttpStatusCode.OK, none.ToJsonString());
        Assert.True(await sync.SyncAsync(ct));
        Assert.Empty(await env.Accounts.GetAccountsAsync(ct));
        Assert.Null(await credentials.GetSecretAsync(anna.Connections[0].Id, ct));
        Assert.Null(await credentials.GetSecretAsync(CloudAccountPasswords.SecretId(pending.CloudId), ct));
    }
}
