using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Core;
using Neruna.Core.Cloud;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public class TextTemplateTests
{
    [Theory]
    [InlineData("tel", "tel")]
    [InlineData(" TEL:: ", "tel")]
    [InlineData("anruf-notiz_2", "anruf-notiz_2")]
    [InlineData("a b!", "ab")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Shortcuts_are_normalised(string? typed, string? expected) =>
        Assert.Equal(expected, TextTemplate.NormalizeShortcut(typed));

    [Fact]
    public async Task Own_templates_win_shortcuts_and_cloud_templates_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var service = new TextTemplateService(env.Get<ITextTemplateStore>());
        await service.SaveAsync(new TextTemplate(Guid.NewGuid(), "Meine Notiz", "<p>meine</p>", DateTimeOffset.Now, Shortcut: "tel"), ct);
        Assert.True(await service.IsShortcutTakenAsync("TEL", Guid.NewGuid(), ct));

        // The organisation offers a template with the same shortcut and one with another.
        using var key = DeviceKey.Create();
        await env.Get<ICredentialStore>().SetSecretAsync(CloudController.KeyId, key.ExportPrivateKey(), ct);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(
            new CloudConnection(new Uri("https://cloud.example/"), "01DEVICE", "Example AG", "Anna", DateTimeOffset.Now), NerunaJson.Options), ct);
        env.Http.Responses["https://cloud.example/api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        env.Http.Responses["https://cloud.example/api/v1/text-templates"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            available = true,
            templates = new[]
            {
                new { id = "A", name = "Anrufnotiz", shortcut = "tel", html = "<p>firma</p>", text = "firma", updatedAt = DateTimeOffset.Now },
                new { id = "B", name = "Absage", shortcut = "nein", html = "<p>leider</p>", text = "leider", updatedAt = DateTimeOffset.Now },
            },
        }));
        var cloud = new CloudController(new HttpClient(env.Http), settings, env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);
        var sync = new CloudTextTemplateSync(cloud, service, NullLogger<CloudTextTemplateSync>.Instance);

        Assert.True(await sync.SyncAsync(ct));
        Assert.Equal(["Absage", "Anrufnotiz", "Meine Notiz"], (await service.GetAllAsync(ct)).Select(t => t.Name));

        var shortcuts = await service.GetShortcutsAsync(ct);
        Assert.Equal("<p>meine</p>", shortcuts["TEL"].Html);     // own wins, any case
        Assert.Equal("<p>leider</p>", shortcuts["nein"].Html);

        Assert.False(await sync.SyncAsync(ct));
        Assert.True(await sync.RemoveAllAsync(ct));
        Assert.Equal(["Meine Notiz"], (await service.GetAllAsync(ct)).Select(t => t.Name));
    }
}
