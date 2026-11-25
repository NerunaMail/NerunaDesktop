using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>Quick actions shared in the organisation: fitted here, switched off here, shared and changed by who may.</summary>
public class SharedQuickActionTests
{
    private const string Url = "https://cloud.example/api/v1/quick-actions";
    private static readonly Guid Shared = Guid.Parse("3f2b8c1e-5a4d-4e7b-9c10-2d6f8a9b0c11");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(CloudQuickActionSync Sync, QuickActionService Actions, StubHttpHandler Http)> SetUpAsync(TestEnvironment env, bool withAccount)
    {
        var settings = env.Get<ISettingsStore>();
        using var key = DeviceKey.Create();
        await env.Get<ICredentialStore>().SetSecretAsync(CloudController.KeyId, key.ExportPrivateKey(), Ct);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(
            new CloudConnection(new Uri("https://cloud.example/"), "01DEVICE", "Example AG", "Bea", DateTimeOffset.Now), NerunaJson.Options), Ct);
        env.Http.Responses["https://cloud.example/api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        if (withAccount)
        {
            await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Info", "INFO@example.com", []), Ct);
        }

        var actions = new QuickActionService(settings);
        var cloud = new CloudController(new HttpClient(env.Http), settings, env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);
        var sync = new CloudQuickActionSync(cloud, actions, env.Accounts, new TextTemplateService(env.Get<ITextTemplateStore>()), NullLogger<CloudQuickActionSync>.Instance);
        return (sync, actions, env.Http);
    }

    private static void Offer(StubHttpHandler http, bool canShare, params object[] actions) =>
        http.Responses[Url] = (HttpStatusCode.OK, JsonSerializer.Serialize(new { available = true, canShare, actions }));

    private static JsonElement Fixture() => JsonSerializer.Deserialize<JsonElement>(ContractFixtures.Read("quick-actions-example.json")).GetProperty("actions")[0];

    [Fact]
    public async Task A_shared_action_arrives_fitted_here_and_stays_off_when_switched_off()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (sync, actions, http) = await SetUpAsync(env, withAccount: true);
        Offer(http, canShare: false, Fixture());

        Assert.True(await sync.SyncAsync(Ct));
        var action = Assert.Single(await actions.GetActionsAsync(Ct));
        var info = (await env.Accounts.GetAccountsAsync(Ct)).Single();
        Assert.Equal((Shared, info.Id, "INBOX/Rechnungen"), (action.Id, action.When.AccountId, action.Steps[1].FolderRemoteId));
        Assert.Equal(("Anna Muster", "Bea Beispiel"), (action.Shared!.SharedBy, action.Shared.UpdatedBy));
        Assert.Contains(await actions.GetStampsAsync(Ct), s => s.Text == "Geprüft");
        Assert.False(sync.CanEdit(action));
        Assert.False(sync.CanShareAny);

        // Off here: kept off through the next syncs; no button.
        await actions.SaveActionsAsync([action with { IsOff = true }], Ct);
        Assert.False(await sync.SyncAsync(Ct));
        var off = Assert.Single(await actions.GetActionsAsync(Ct));
        Assert.True(off.IsOff);
        Assert.False(QuickActionService.Matches(off, new QuickActionMessage(info.Id, "INBOX/Rechnungen", "x@lieferant.ch", "Rechnung", [new("r.pdf", "application/pdf")])));

        // No longer offered (deleted or switched off in the portal): gone; own actions stay.
        var own = new QuickAction(Guid.NewGuid(), "Eigene", "#107C10", new QuickActionCondition(), []);
        await actions.SaveActionsAsync([off, own], Ct);
        Offer(http, canShare: false);
        Assert.True(await sync.SyncAsync(Ct));
        Assert.Equal([own.Id], (await actions.GetActionsAsync(Ct)).Select(a => a.Id));
    }

    [Fact]
    public async Task For_an_account_not_set_up_here_the_button_never_appears()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (sync, actions, http) = await SetUpAsync(env, withAccount: false);
        Offer(http, canShare: true, Fixture());

        await sync.SyncAsync(Ct);
        var action = Assert.Single(await actions.GetActionsAsync(Ct));
        Assert.Equal("info@example.com", action.When.MissingAccount);
        Assert.False(QuickActionService.Matches(action, new QuickActionMessage(Guid.NewGuid(), null, "x@lieferant.ch", "Rechnung", [new("r.pdf", "application/pdf")])));
    }

    [Fact]
    public async Task Who_may_share_shares_and_changes_shared_actions()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (sync, actions, http) = await SetUpAsync(env, withAccount: true);
        Offer(http, canShare: true);
        await sync.SyncAsync(Ct);
        Assert.True(sync.CanShareAny);

        var own = new QuickAction(Guid.NewGuid(), "Ablegen", "#107C10", new QuickActionCondition { Attachment = QuickActionAttachmentKind.Pdf }, [new(QuickActionStepKind.MarkRead)]);
        await actions.SaveActionsAsync([own], Ct);
        var put = $"{Url}/{own.Id}";
        http.Responses[put] = (HttpStatusCode.Created, JsonSerializer.Serialize(new { id = own.Id.ToString(), label = "Ablegen", content = "{}", sharedBy = "Bea Muster", updatedBy = (string?)null, updatedAt = DateTimeOffset.Now }));

        var shared = await sync.ShareAsync(own, Ct);
        var (method, url, body) = Assert.Single(http.Sent, s => s.Url == put);
        Assert.Equal("PUT", method);
        using (var request = JsonDocument.Parse(body))
        {
            Assert.Equal("Ablegen", request.RootElement.GetProperty("label").GetString());
            var (imported, _) = QuickActionService.Import(request.RootElement.GetProperty("content").GetString()!, new Dictionary<Guid, string>(), new Dictionary<Guid, string>());
            Assert.Equal(own.Id, Assert.Single(imported).Id);
        }

        Assert.Equal("Bea Muster", shared.Shared!.SharedBy);
        Assert.True(sync.CanEdit(shared));
        Assert.Equal(shared.Shared, Assert.Single(await actions.GetActionsAsync(Ct)).Shared);

        // Stop sharing: stays here as an own action.
        http.Responses[put] = (HttpStatusCode.NoContent, string.Empty);
        var back = await sync.UnshareAsync(shared, Ct);
        Assert.Null(back.Shared);
        Assert.Contains(http.Requested, r => r == put);
        Assert.Null(Assert.Single(await actions.GetActionsAsync(Ct)).Shared);
    }
}
