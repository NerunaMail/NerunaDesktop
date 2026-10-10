using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Cloud;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>Manual settings backups in the config vault: zero knowledge, personal things only, preview then restore.</summary>
public class SettingsBackupTests
{
    private const string Server = "https://cloud.example/";

    private static readonly Lazy<(Org.BouncyCastle.X509.X509Certificate Certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter PrivateKey)> Anna =
        new(() => TestPki.User("Anna Muster", "anna@example.com"));

    [Fact]
    public async Task Backup_on_one_computer_restores_on_a_new_one_with_a_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeVaultServer();
        await using var office = await TestEnvironment.CreateAsync();
        await using var laptop = await TestEnvironment.CreateAsync();
        var officeBackup = await ConnectAsync(office, server, "Büro-PC");
        var laptopBackup = await ConnectAsync(laptop, server, "Laptop");

        // Office: an account with password, an own signature and template, an own certificate – plus things from the cloud.
        var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, FakeMailProviderFactory.Id, new Dictionary<string, string> { ["imap.host"] = "mail.example.com" });
        var account = new Account(Guid.NewGuid(), "Anna Muster", "anna@example.com", [mail], "Privat");
        await office.Accounts.SaveAccountAsync(account, ct);
        await office.Get<ICredentialStore>().SetSecretAsync(mail.Id, "geheim", ct);
        var signature = new Signature(Guid.NewGuid(), "Kurz", "<p>Gruss Anna</p>", DateTimeOffset.UnixEpoch);
        await office.Get<ISignatureStore>().SaveAsync(signature, ct);
        await office.Get<ISignatureStore>().SaveAsync(new Signature(Guid.NewGuid(), "Firma", "<p>Example AG</p>", DateTimeOffset.UnixEpoch, Signature.CloudSource), ct);
        await office.Get<ITextTemplateStore>().SaveAsync(new TextTemplate(Guid.NewGuid(), "Danke", "<p>Danke!</p>", DateTimeOffset.UnixEpoch, Shortcut: "dk"), ct);
        await office.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "p12"), "p12", cancellationToken: ct);
        await office.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(TestPki.User("Firma", "info@example.com"), "x"), "x", CertificateSource.Cloud, ct);
        await office.Get<ISettingsStore>().SetAsync(SettingKeys.SmimeCipher, "aes128", ct);
        await office.Get<ISettingsStore>().SetAsync("ui.layout", "{\"width\":1600}", ct);

        // What came later: out-of-office templates and text, calendars and task lists switched off or shown in the
        // calendar, own colours and names – all personal settings, so all in the backup.
        var officeSettings = office.Get<ISettingsStore>();
        await AutoReplyText.SaveTemplateAsync(officeSettings, new AutoReplyTemplate("Ferien", "Bis {{end}} in den Ferien."), ct);
        await AutoReplyText.RememberAsync(officeSettings, mail.Id, "Bei Kunden bis {{end}}.", ct);
        var team = new CalendarInfo(mail.Id, "team", "Team", null, false, Content: CalendarContent.Events | CalendarContent.Tasks);
        await office.Calendar.SetCalendarVisibleAsync(team, false, ct);
        await office.Tasks.SetListVisibleAsync(team, false, ct);
        await office.Tasks.SetListInCalendarAsync(team, true, ct);
        await office.Calendar.SetDisplayNameAsync(team, "Team Bernasconi", ct);
        await officeSettings.SetAsync($"calendar.color.{mail.Id:N}.team", "#C239B3", ct);

        // No vault yet: nothing to suggest; set up with password, then a backup with a note.
        Assert.False(await officeBackup.ShouldSuggestBackupAsync(ct));
        var recoveryCode = await officeBackup.SetUpAsync("Tresor-Passwort 2026", ct);
        Assert.True(await officeBackup.ShouldSuggestBackupAsync(ct));
        await officeBackup.CreateBackupAsync("Erste Sicherung", ct);
        Assert.False(await officeBackup.ShouldSuggestBackupAsync(ct));

        // The server only ever saw ciphertext: no password, no note, no name.
        var stored = server.Dump();
        foreach (var secret in new[] { "geheim", "Gruss Anna", "Erste Sicherung", "Privat", "Tresor-Passwort", "mail.example.com" })
        {
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        }

        // A change → suggest; "Später" → quiet until the next change.
        await office.Get<ISettingsStore>().SetAsync(SettingKeys.SmimeDigest, "sha512", ct);
        Assert.True(await officeBackup.ShouldSuggestBackupAsync(ct));
        await officeBackup.DismissSuggestionAsync(ct);
        Assert.False(await officeBackup.ShouldSuggestBackupAsync(ct));
        await office.Get<ISettingsStore>().SetAsync("ui.layout", "{\"width\":800}", ct); // device only: no reason
        Assert.False(await officeBackup.ShouldSuggestBackupAsync(ct));

        // The new laptop: the backups are listed, notes only once unlocked; a wrong password changes nothing.
        var locked = await laptopBackup.GetOverviewAsync(ct);
        Assert.True(locked.HasVault);
        Assert.False(locked.IsUnlocked);
        Assert.Null(Assert.Single(locked.Backups).Note);
        await Assert.ThrowsAsync<BackupException>(() => laptopBackup.UnlockAsync("falsch", ct));
        await laptopBackup.UnlockAsync(recoveryCode, ct);
        var overview = await laptopBackup.GetOverviewAsync(ct);
        Assert.True(overview.IsUnlocked);
        var entry = Assert.Single(overview.Backups);
        Assert.Equal(("Erste Sicherung", "Büro-PC"), (entry.Note, entry.DeviceName));

        // The laptop already has something of its own: the preview says what comes and what goes.
        var old = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, FakeMailProviderFactory.Id, new Dictionary<string, string>());
        await laptop.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Alt", "alt@example.com", [old]), ct);
        await laptop.Get<ICredentialStore>().SetSecretAsync(old.Id, "alt", ct);
        var plan = await laptopBackup.PrepareRestoreAsync(entry.Id, ct);
        Assert.Contains(new BackupChange("Konten", "Privat", BackupChangeKind.Added), plan.Changes);
        Assert.Contains(new BackupChange("Konten", "alt@example.com", BackupChangeKind.Removed), plan.Changes);
        Assert.Contains(new BackupChange("Signaturen", "Kurz", BackupChangeKind.Added), plan.Changes);
        Assert.Contains(new BackupChange("Textvorlagen", "Danke", BackupChangeKind.Added), plan.Changes);
        Assert.Contains(new BackupChange("Zertifikate", "anna@example.com", BackupChangeKind.Added), plan.Changes);
        Assert.DoesNotContain(plan.Changes, c => c.Name.Contains("Firma", StringComparison.Ordinal) || c.Name.Contains("info@", StringComparison.Ordinal));
        Assert.Single(await laptop.Accounts.GetAccountsAsync(ct)); // nothing changed yet

        await laptopBackup.RestoreAsync(plan, ct);

        var restored = Assert.Single(await laptop.Accounts.GetAccountsAsync(ct));
        Assert.Equal((account.Id, "Privat", mail.Id), (restored.Id, restored.Title, restored.Connections.Single().Id));
        Assert.Equal("geheim", await laptop.Get<ICredentialStore>().GetSecretAsync(mail.Id, ct));
        Assert.Null(await laptop.Get<ICredentialStore>().GetSecretAsync(old.Id, ct));
        Assert.Equal("Kurz", Assert.Single(await laptop.Get<ISignatureStore>().GetAllAsync(ct)).Name);
        Assert.Equal("dk", Assert.Single(await laptop.Get<ITextTemplateStore>().GetAllAsync(ct)).Shortcut);
        var certificate = Assert.Single(await laptop.Get<CertificateManager>().GetAllAsync(ct));
        Assert.True(certificate.HasPrivateKey);
        Assert.Single((await laptop.Get<CertificateManager>().LoadMaterialAsync(ct)).PrivateKeys); // with its password
        Assert.Equal("aes128", await laptop.Get<ISettingsStore>().GetAsync(SettingKeys.SmimeCipher, ct));
        var laptopSettings = laptop.Get<ISettingsStore>();
        Assert.Equal("Bis {{end}} in den Ferien.", Assert.Single(await AutoReplyText.LoadTemplatesAsync(laptopSettings, ct)).Text);
        Assert.Equal("Bei Kunden bis {{end}}.", await AutoReplyText.TemplateForAsync(laptopSettings, mail.Id, new AutoReply(false, "Bei Kunden bis {{end}}."), ct));
        Assert.Contains(CalendarController.CalendarKey(team), await laptop.Calendar.GetHiddenCalendarsAsync(ct));
        Assert.Contains(CalendarController.CalendarKey(team), await laptop.Tasks.GetHiddenListsAsync(ct));
        Assert.Contains(CalendarController.CalendarKey(team), await laptop.Tasks.GetListsInCalendarAsync(ct));
        Assert.Equal("#C239B3", await laptopSettings.GetAsync($"calendar.color.{mail.Id:N}.team", ct));
        Assert.NotNull(await laptopSettings.GetAsync($"calendar.name.{mail.Id:N}.team", ct));
        Assert.Null(await laptop.Get<ISettingsStore>().GetAsync("ui.layout", ct));
        Assert.False(await laptopBackup.ShouldSuggestBackupAsync(ct));
    }

    [Fact]
    public async Task Twenty_backups_are_kept_and_a_lost_vault_can_be_started_over()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeVaultServer();
        await using var env = await TestEnvironment.CreateAsync();
        var backup = await ConnectAsync(env, server, "Büro-PC");
        await backup.SetUpAsync("Passwort 1", ct);
        for (var i = 1; i <= 21; i++)
        {
            await backup.CreateBackupAsync($"Nr. {i}", ct);
        }

        var overview = await backup.GetOverviewAsync(ct);
        Assert.Equal(20, overview.Backups.Count);
        Assert.Equal("Nr. 21", overview.Backups[0].Note);
        Assert.DoesNotContain(overview.Backups, b => b.Note == "Nr. 1");

        await Assert.ThrowsAsync<BackupException>(() => backup.SetUpAsync("Passwort 2", ct));
        await backup.ResetAsync(ct);
        Assert.False((await backup.GetOverviewAsync(ct)).HasVault);
        await backup.SetUpAsync("Passwort 2", ct);
        Assert.Empty((await backup.GetOverviewAsync(ct)).Backups);
    }

    private static async Task<SettingsBackupService> ConnectAsync(TestEnvironment env, FakeVaultServer server, string device)
    {
        var settings = env.Get<ISettingsStore>();
        var credentials = env.Get<ICredentialStore>();
        using var key = DeviceKey.Create();
        await credentials.SetSecretAsync(CloudController.KeyId, key.ExportPrivateKey());
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(
            new CloudConnection(new Uri(Server), device, "Example AG", "Anna Muster", DateTimeOffset.Now), NerunaJson.Options));
        var cloud = new CloudController(new HttpClient(server), settings, credentials, NullLogger<CloudController>.Instance);
        return new SettingsBackupService(cloud, env.Accounts, credentials, settings, env.Get<ISignatureStore>(), env.Get<ITextTemplateStore>(),
            env.Get<CertificateManager>(), TimeProvider.System, NullLogger<SettingsBackupService>.Instance);
    }

    /// <summary>The vault part of the server API in memory (one person); the device name is the device id here.</summary>
    private sealed class FakeVaultServer : HttpMessageHandler
    {
        private readonly List<JsonObject> _backups = [];
        private JsonObject? _vault;
        private int _next;

        public string Dump() => (_vault?.ToJsonString() ?? string.Empty) + string.Join(string.Empty, _backups.Select(b => b.ToJsonString()));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
            var device = request.Headers.Authorization?.Parameter ?? "?";
            return (request.Method.Method, path) switch
            {
                ("POST", "/api/v1/token") => Json(new JsonObject { ["accessToken"] = DeviceOf(body!), ["tokenType"] = "Bearer", ["expiresIn"] = 900 }),
                ("GET", "/api/v1/vault") => Json(new JsonObject
                {
                    ["vault"] = _vault?.DeepClone(),
                    ["backups"] = new JsonArray([.. _backups.AsEnumerable().Reverse().Select(Summary)]),
                    ["maxBackups"] = 20,
                }),
                ("PUT", "/api/v1/vault") when _vault is not null && _vault["vaultId"]!.GetValue<string>() != body!["vaultId"]!.GetValue<string>() =>
                    Json(new JsonObject { ["error"] = "vault_exists", ["message"] = "Tresor vorhanden" }, HttpStatusCode.Conflict),
                ("PUT", "/api/v1/vault") => Json((_vault = new JsonObject { ["vaultId"] = body!["vaultId"]!.GetValue<string>(), ["keys"] = body["keys"]!.DeepClone(), ["updatedAt"] = DateTimeOffset.UtcNow }).DeepClone()),
                ("DELETE", "/api/v1/vault") => Clear(),
                ("POST", "/api/v1/vault/backups") => Store(body!, device),
                ("GET", _) when Find(path) is { } backup => Json(backup.DeepClone()),
                ("DELETE", _) when Find(path) is { } backup => Remove(backup),
                _ => Json(new JsonObject { ["error"] = "backup_not_found", ["message"] = "nicht gefunden" }, HttpStatusCode.NotFound),
            };
        }

        private static string DeviceOf(JsonObject body)
        {
            var assertion = body["assertion"]!.GetValue<string>().Split('.')[0].Replace('-', '+').Replace('_', '/');
            var header = JsonNode.Parse(Convert.FromBase64String(assertion.PadRight((assertion.Length + 3) / 4 * 4, '=')))!;
            return header["kid"]!.GetValue<string>();
        }

        private HttpResponseMessage Store(JsonObject body, string device)
        {
            var backup = new JsonObject
            {
                ["id"] = $"B{++_next:D25}",
                ["createdAt"] = DateTimeOffset.UtcNow.AddSeconds(_next),
                ["deviceName"] = device,
                ["note"] = body["note"]?.DeepClone(),
                ["size"] = body["ciphertext"]!.GetValue<string>().Length * 3 / 4,
                ["vaultId"] = body["vaultId"]!.DeepClone(),
                ["version"] = body["version"]!.DeepClone(),
                ["nonce"] = body["nonce"]!.DeepClone(),
                ["ciphertext"] = body["ciphertext"]!.DeepClone(),
            };
            _backups.Add(backup);
            if (_backups.Count > 20)
            {
                _backups.RemoveAt(0);
            }

            return Json(Summary(backup), HttpStatusCode.Created);
        }

        private HttpResponseMessage Clear()
        {
            _vault = null;
            _backups.Clear();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private HttpResponseMessage Remove(JsonObject backup)
        {
            _backups.Remove(backup);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private JsonObject? Find(string path) =>
            path.StartsWith("/api/v1/vault/backups/", StringComparison.Ordinal) ? _backups.FirstOrDefault(b => "/api/v1/vault/backups/" + b["id"]!.GetValue<string>() == path) : null;

        private static JsonObject Summary(JsonObject backup) => new()
        {
            ["id"] = backup["id"]!.DeepClone(),
            ["createdAt"] = backup["createdAt"]!.DeepClone(),
            ["deviceName"] = backup["deviceName"]!.DeepClone(),
            ["note"] = backup["note"]?.DeepClone(),
            ["size"] = backup["size"]!.DeepClone(),
        };

        private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}
