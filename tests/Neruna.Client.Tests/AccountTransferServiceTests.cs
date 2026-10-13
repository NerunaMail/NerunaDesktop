using Microsoft.Extensions.DependencyInjection;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using Neruna.Core.Providers;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public sealed class AccountTransferServiceTests
{
    [Fact]
    public async Task Mail_calendars_and_contacts_reach_the_phone_with_their_passwords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var desktop = await TestEnvironment.CreateAsync(s => s.AddSingleton<AccountTransferService>());
        await using var phone = await TestEnvironment.CreateAsync(s => s.AddSingleton<AccountTransferService>());
        var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, new Dictionary<string, string> { ["host"] = "imap.example.com" });
        var calendar = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.CalDav, new Dictionary<string, string> { ["url"] = "https://dav.example.com/" });
        var contacts = new ServiceConnection(Guid.NewGuid(), ServiceKind.Contacts, ProviderIds.CardDav, new Dictionary<string, string> { ["url"] = "https://dav.example.com/" });
        var ics = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.Ics, new Dictionary<string, string>());
        var account = new Account(Guid.NewGuid(), "Anna", "anna@example.com", [mail, calendar, contacts, ics]);
        await desktop.Accounts.SaveAccountAsync(account, ct);
        var credentials = desktop.Get<ICredentialStore>();
        await credentials.SetSecretAsync(mail.Id, "mail-pw", ct);
        await credentials.SetSecretAsync(calendar.Id, "dav-pw", ct);
        var settings = desktop.Get<ISettingsStore>();
        await settings.SetAsync($"calendars.selected.{calendar.Id:N}", "[\"/cal/work/\"]", ct);
        await settings.SetAsync("unrelated.setting", "x", ct);

        var code = await desktop.Get<AccountTransferService>().CreateCodeAsync(account, ct);
        var imported = await phone.Get<AccountTransferService>().ImportAsync(code.Code, code.Pin, ct);

        Assert.Equal(account.Id, imported.Id);
        Assert.Equal([ServiceKind.Mail, ServiceKind.Calendar, ServiceKind.Contacts], imported.Connections.Select(c => c.Kind));
        Assert.Equal("mail-pw", await phone.Get<ICredentialStore>().GetSecretAsync(mail.Id, ct));
        Assert.Equal("dav-pw", await phone.Get<ICredentialStore>().GetSecretAsync(calendar.Id, ct));
        await Assert.ThrowsAsync<AccountTransferException>(() => phone.Get<AccountTransferService>().ImportAsync(code.Code, "000000" == code.Pin ? "111111" : "000000", ct));
    }

    [Fact]
    public async Task A_typical_account_fits_a_code_a_phone_reads_off_the_screen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var desktop = await TestEnvironment.CreateAsync(s => s.AddSingleton<AccountTransferService>());
        var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, new Dictionary<string, string>
        {
            ["imap.host"] = "mail.example-hosting.ch", ["imap.port"] = "993", ["imap.security"] = "SslOnConnect",
            ["smtp.host"] = "mail.example-hosting.ch", ["smtp.port"] = "587", ["smtp.security"] = "StartTls", ["username"] = "anna.muster@example.ch",
        });
        var calendar = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.CalDav,
            new Dictionary<string, string> { ["url"] = "https://dav.example-hosting.ch/SOGo/dav/anna.muster@example.ch/", ["username"] = "anna.muster@example.ch" });
        var contacts = new ServiceConnection(Guid.NewGuid(), ServiceKind.Contacts, ProviderIds.CardDav,
            new Dictionary<string, string> { ["url"] = "https://dav.example-hosting.ch/SOGo/dav/anna.muster@example.ch/", ["username"] = "anna.muster@example.ch" });
        var account = new Account(Guid.NewGuid(), "Anna Muster", "anna.muster@example.ch", [mail, calendar, contacts], "Privat");
        await desktop.Accounts.SaveAccountAsync(account, ct);
        foreach (var connection in account.Connections)
        {
            await desktop.Get<ICredentialStore>().SetSecretAsync(connection.Id, "Ein-langes-Passwort-2026!", ct);
        }

        var settings = desktop.Get<ISettingsStore>();
        await settings.SetAsync($"calendars.selected.{calendar.Id:N}",
            "[\"/SOGo/dav/anna.muster@example.ch/Calendar/personal/\",\"/SOGo/dav/anna.muster@example.ch/Calendar/7A1-65F3B2C0-1-4B1E7D80/\",\"/SOGo/dav/anna.muster@example.ch/Calendar/Familie/\"]", ct);
        await settings.SetAsync($"calendars.known.{calendar.Id:N}", "[\"" + string.Join("\",\"", Enumerable.Range(0, 10).Select(i => $"/SOGo/dav/anna.muster@example.ch/Calendar/{i}/")) + "\"]", ct);
        await settings.SetAsync($"addressbooks.selected.{contacts.Id:N}", "[\"/SOGo/dav/anna.muster@example.ch/Contacts/personal/\"]", ct);

        var code = await desktop.Get<AccountTransferService>().CreateCodeAsync(account, ct);

        Assert.True(code.Code.Length < 1000, $"{code.Code.Length} characters");
    }
}
