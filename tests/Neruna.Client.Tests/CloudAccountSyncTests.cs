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
        var cloud = new CloudController(new HttpClient(env.Http), settings, credentials, NullLogger<CloudController>.Instance);
        var registry = new ProviderRegistry(
            [new ImapProviderFactory(credentials, NullLoggerFactory.Instance)],
            [new CalDavProviderFactory(new HttpClient(env.Http), credentials, NullLoggerFactory.Instance)],
            []);
        var setup = new AccountSetupService(registry, env.Accounts, credentials, []);
        var sync = new CloudAccountSync(cloud, env.Accounts, setup, credentials, settings, NullLogger<CloudAccountSync>.Instance);

        // This app asks for what it can set up: IMAP and – with the CalDAV provider – calendars and contacts only.
        Assert.Equal("imap,dav", sync.SupportedKinds);
        var accountsUrl = Server + "api/v1/mail-accounts?kinds=imap%2Cdav";
        env.Http.Responses[accountsUrl] = (HttpStatusCode.OK, response);

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
        env.Http.Responses[accountsUrl] = (HttpStatusCode.OK, forged.ToJsonString());
        await sync.SyncAsync(ct);
        Assert.Equal(1, sync.Status.Waiting);

        // With the password the user typed, the next sync sets info@ up too.
        await CloudAccountPasswords.SetAsync(credentials, env.Accounts, pending.CloudId, "info-pw", ct);
        env.Http.Responses[accountsUrl] = (HttpStatusCode.OK, response);
        await sync.SyncAsync(ct);
        Assert.Empty(sync.Status.Pending);
        Assert.Equal(2, (await env.Accounts.GetAccountsAsync(ct)).Count);

        // info@ answers to Anna: the organisation's Antwortadresse, fixed on this device; Anna's own account has none.
        var info = (await env.Accounts.GetAccountsAsync(ct)).Single(a => a.EmailAddress == "info@example.com");
        Assert.Equal(("anna@example.com", true), await Neruna.Core.Mail.ReplyAddress.GetAsync(settings, info, ct));
        await Neruna.Core.Mail.ReplyAddress.SetAsync(settings, info.Id, "someone@example.com", ct);
        Assert.Equal(("anna@example.com", true), await Neruna.Core.Mail.ReplyAddress.GetAsync(settings, info, ct));
        Assert.Equal(((string?)null, false), await Neruna.Core.Mail.ReplyAddress.GetAsync(settings, again, ct));

        // That typed password goes into the personal backup; the organisation's accounts themselves do not.
        var backup = new SettingsBackupService(cloud, env.Accounts, credentials, settings, env.Get<Neruna.Core.Mail.ISignatureStore>(), env.Get<Neruna.Core.Mail.ITextTemplateStore>(),
            env.Get<CertificateManager>(), TimeProvider.System, NullLogger<SettingsBackupService>.Instance);
        var content = await backup.CollectAsync(ct);
        Assert.Empty(content.Accounts);
        Assert.Equal("info-pw", Assert.Single(content.CloudPasswords!).Value);

        // No longer assigned: removed with its passwords.
        var none = JsonNode.Parse(response)!;
        none["accounts"] = new JsonArray();
        env.Http.Responses[accountsUrl] = (HttpStatusCode.OK, none.ToJsonString());
        Assert.True(await sync.SyncAsync(ct));
        Assert.Empty(await env.Accounts.GetAccountsAsync(ct));
        Assert.Null(await credentials.GetSecretAsync(anna.Connections[0].Id, ct));
        Assert.Null(await credentials.GetSecretAsync(CloudAccountPasswords.SecretId(pending.CloudId), ct));
    }

    [Fact]
    public async Task An_account_listed_as_another_kind_than_it_contains_is_not_set_up()
    {
        // "Calendars only" does not count as a mailbox: an Exchange mailbox listed that way must not slip through.
        var ct = TestContext.Current.CancellationToken;
        var response = ContractFixtures.Read("mail-accounts-kinds-example.json").Replace("\"kind\": \"exchange\"", "\"kind\": \"dav\"", StringComparison.Ordinal);
        var device = JsonDocument.Parse(ContractFixtures.Read("certificates-test-device.json")).RootElement;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var credentials = env.Get<ICredentialStore>();
        using var signingKey = DeviceKey.Create();
        await credentials.SetSecretAsync(CloudController.KeyId, signingKey.ExportPrivateKey(), ct);
        await credentials.SetSecretAsync(CloudController.EncryptionKeyId, device.GetProperty("privateKey").GetString()!, ct);
        var connection = new CloudConnection(new Uri(Server), device.GetProperty("deviceId").GetString()!, "Example AG", "Anna Muster", DateTimeOffset.Now);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(connection, NerunaJson.Options), ct);
        env.Http.Responses[Server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");

        var cloud = new CloudController(new HttpClient(env.Http), settings, credentials, NullLogger<CloudController>.Instance);
        var ews = new Neruna.Providers.Ews.EwsConnectionFactory(credentials, new ClientCertificates(credentials), NullLoggerFactory.Instance);
        var registry = new ProviderRegistry(
            [new ImapProviderFactory(credentials, NullLoggerFactory.Instance), new Neruna.Providers.Ews.EwsMailProviderFactory(ews)],
            [new CalDavProviderFactory(new HttpClient(env.Http), credentials, NullLoggerFactory.Instance), new Neruna.Providers.Ews.EwsCalendarProviderFactory(ews)],
            [new Neruna.Providers.Ews.EwsContactProviderFactory(ews)]);
        var sync = new CloudAccountSync(cloud, env.Accounts, new AccountSetupService(registry, env.Accounts, credentials, []), credentials, settings, NullLogger<CloudAccountSync>.Instance);
        env.Http.Responses[Server + "api/v1/mail-accounts?kinds=" + Uri.EscapeDataString(sync.SupportedKinds)] = (HttpStatusCode.OK, response);

        await sync.SyncAsync(ct);

        var accounts = await env.Accounts.GetAccountsAsync(ct);
        Assert.DoesNotContain(accounts, a => a.EmailAddress == "anna@exchange.example.com");
        Assert.Contains(accounts, a => a.DisplayName == "Kalender");
    }

    [Fact]
    public async Task Other_kinds_need_the_user_once_and_keep_the_organisations_names_and_signatures()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = ContractFixtures.Read("mail-accounts-kinds-example.json");
        var device = JsonDocument.Parse(ContractFixtures.Read("certificates-test-device.json")).RootElement;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var credentials = env.Get<ICredentialStore>();
        using var signingKey = DeviceKey.Create();
        await credentials.SetSecretAsync(CloudController.KeyId, signingKey.ExportPrivateKey(), ct);
        await credentials.SetSecretAsync(CloudController.EncryptionKeyId, device.GetProperty("privateKey").GetString()!, ct);
        var connection = new CloudConnection(new Uri(Server), device.GetProperty("deviceId").GetString()!, "Example AG", "Anna Muster", DateTimeOffset.Now);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(connection, NerunaJson.Options), ct);
        env.Http.Responses[Server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");

        var cloud = new CloudController(new HttpClient(env.Http), settings, credentials, NullLogger<CloudController>.Instance);
        var ews = new Neruna.Providers.Ews.EwsConnectionFactory(credentials, new ClientCertificates(credentials), NullLoggerFactory.Instance);
        var registry = new ProviderRegistry(
            [new ImapProviderFactory(credentials, NullLoggerFactory.Instance), new Neruna.Providers.Ews.EwsMailProviderFactory(ews)],
            [new CalDavProviderFactory(new HttpClient(env.Http), credentials, NullLoggerFactory.Instance), new Neruna.Providers.Ews.EwsCalendarProviderFactory(ews)],
            [new Neruna.Providers.Ews.EwsContactProviderFactory(ews)]);
        var setup = new AccountSetupService(registry, env.Accounts, credentials, []);
        var sync = new CloudAccountSync(cloud, env.Accounts, setup, credentials, settings, NullLogger<CloudAccountSync>.Instance)
        {
            InteractiveKinds = [CloudAccountKinds.Microsoft365, CloudAccountKinds.Proton, CloudAccountKinds.Nextcloud],
        };
        Assert.Equal("imap,exchange,dav,microsoft365,proton,nextcloud", sync.SupportedKinds);
        env.Http.Responses[Server + "api/v1/mail-accounts?kinds=" + Uri.EscapeDataString(sync.SupportedKinds)] = (HttpStatusCode.OK, response);

        // Exchange and CalDAV came with their passwords: set up at once.
        await sync.SyncAsync(ct);
        var accounts = await env.Accounts.GetAccountsAsync(ct);
        var exchange = accounts.Single(a => a.EmailAddress == "anna@exchange.example.com");
        Assert.All(exchange.Connections, c => Assert.Equal(ProviderIds.Ews, c.ProviderId));
        Assert.Equal("Basic", exchange.Connections[0].Settings["ews.auth"]);
        var dav = accounts.Single(a => a.DisplayName == "Kalender");
        Assert.Equal([ServiceKind.Calendar], dav.Connections.Select(c => c.Kind));
        Assert.Equal("dav-pw", await credentials.GetSecretAsync(dav.Connections[0].Id, ct));

        // Microsoft 365 and Nextcloud wait for a sign-in, Proton for the Bridge password of this computer.
        Assert.Equal([(CloudAccountKinds.Microsoft365, true), (CloudAccountKinds.Nextcloud, true), (CloudAccountKinds.Proton, false)],
            sync.Status.Pending.Select(p => (p.Kind, p.SignsIn)).OrderBy(p => p.Kind == CloudAccountKinds.Proton).ThenBy(p => p.Kind));
        var proton = sync.Status.Pending.Single(p => p.Kind == CloudAccountKinds.Proton).Account;
        Assert.Equal("127.0.0.1", proton.ConnectionsOf(ServiceKind.Mail).Single().Settings["imap.host"]);
        Assert.Equal(["anna@example.ch", "hallo@example.ch"], proton.Identities.Select(i => i.Email));
        Assert.Equal("https://cloud.example.com", sync.Status.Pending.Single(p => p.Kind == CloudAccountKinds.Nextcloud).Server);

        // Signed in with another address: refused and removed again.
        var m365 = sync.Status.Pending.Single(p => p.Kind == CloudAccountKinds.Microsoft365);
        Account SignedIn(string email) => new(Guid.NewGuid(), "Anna", email,
            [new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Graph, new Dictionary<string, string>())]);
        var wrong = SignedIn("someone@example.org");
        await env.Accounts.SaveAccountAsync(wrong, ct);
        await Assert.ThrowsAsync<AccountSetupException>(() => sync.AdoptAsync(m365, wrong, ct));
        Assert.DoesNotContain(await env.Accounts.GetAccountsAsync(ct), a => a.Id == wrong.Id);

        // With the right one: the organisation's account with its name, reply address and signatures; no longer pending.
        var right = SignedIn("Anna@Example.onmicrosoft.com");
        await env.Accounts.SaveAccountAsync(right, ct);
        await sync.AdoptAsync(m365, right, ct);
        var adopted = (await env.Accounts.GetAccountsAsync(ct)).Single(a => a.Id == right.Id);
        Assert.Equal((m365.CloudId, "Anna Muster"), (adopted.CloudId, adopted.DisplayName));
        Assert.Equal(("anna@example.com", true), await Neruna.Core.Mail.ReplyAddress.GetAsync(settings, adopted, ct));
        var signatures = new Neruna.Core.Mail.SignatureService(env.Get<Neruna.Core.Mail.ISignatureStore>(), settings);
        var assignment = await signatures.GetAssignmentAsync(adopted.Id, ct);
        Assert.Equal((CloudSignatureSync.LocalId("01K7SIG0000000000000000000", connection), CloudSignatureSync.LocalId("01K7SIG0000000000000000001", connection)),
            (assignment.NewMessages, assignment.RepliesAndForwards));
        Assert.DoesNotContain(sync.Status.Pending, p => p.Kind == CloudAccountKinds.Microsoft365);

        // An older app (IMAP only) asks without ?kinds and gets none of these.
        Assert.Equal("imap", new CloudAccountSync(cloud, env.Accounts, new AccountSetupService(new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []), env.Accounts, credentials, []),
            credentials, settings, NullLogger<CloudAccountSync>.Instance).SupportedKinds);
    }
}
