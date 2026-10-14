using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public sealed class BackupFileTests
{
    private static SettingsBackupService Backup(TestEnvironment env) => new(
        new CloudController(new HttpClient(env.Http), env.Get<ISettingsStore>(), env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance),
        env.Accounts, env.Get<ICredentialStore>(), env.Get<ISettingsStore>(), env.Get<ISignatureStore>(), env.Get<ITextTemplateStore>(),
        env.Get<CertificateManager>(), TimeProvider.System, NullLogger<SettingsBackupService>.Instance);

    [Fact]
    public async Task Settings_move_to_another_computer_by_a_password_protected_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var oldComputer = await TestEnvironment.CreateAsync();
        await using var newComputer = await TestEnvironment.CreateAsync();
        var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, new Dictionary<string, string> { ["imap.host"] = "imap.example.com" });
        await oldComputer.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", "anna@example.com", [mail]), ct);
        await oldComputer.Get<ICredentialStore>().SetSecretAsync(mail.Id, "geheim", ct);
        await oldComputer.Get<ISettingsStore>().SetAsync("calendar.visibleFrom", "8", ct);

        using var file = new MemoryStream();
        await Backup(oldComputer).ExportToFileAsync(file, "ein sicheres Passwort", ct);
        Assert.DoesNotContain("geheim", System.Text.Encoding.UTF8.GetString(file.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain("imap.example.com", System.Text.Encoding.UTF8.GetString(file.ToArray()), StringComparison.Ordinal);

        // A wrong password opens nothing.
        file.Position = 0;
        await Assert.ThrowsAsync<BackupException>(() => Backup(newComputer).PrepareRestoreFromFileAsync(file, "falsches Passwort!", ct));

        file.Position = 0;
        var plan = await Backup(newComputer).PrepareRestoreFromFileAsync(file, "ein sicheres Passwort", ct);
        Assert.Contains(plan.Changes, c => c.Name == "anna@example.com");
        await Backup(newComputer).RestoreAsync(plan, ct);

        var restored = Assert.Single(await newComputer.Accounts.GetAccountsAsync(ct));
        Assert.Equal("anna@example.com", restored.EmailAddress);
        Assert.Equal("geheim", await newComputer.Get<ICredentialStore>().GetSecretAsync(mail.Id, ct));
        Assert.Equal("8", await newComputer.Get<ISettingsStore>().GetAsync("calendar.visibleFrom", ct));
    }

    [Fact]
    public async Task A_short_password_is_refused()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var file = new MemoryStream();
        await Assert.ThrowsAsync<BackupException>(() => Backup(env).ExportToFileAsync(file, "kurz", TestContext.Current.CancellationToken));
    }
}
