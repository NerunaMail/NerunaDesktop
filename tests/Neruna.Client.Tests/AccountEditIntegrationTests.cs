using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>
/// "Konto bearbeiten" against the test lab (GreenMail checks passwords, Radicale accepts any):
///   docker-compose -f tools/testlab/docker-compose.yml up -d &amp;&amp; tools/testlab/seed.sh
///   NERUNA_TEST_IMAP_HOST=127.0.0.1 NERUNA_TEST_DAV_URL=http://127.0.0.1:5232/ dotnet test
/// </summary>
[Collection(Testlab.Name)]
public class AccountEditIntegrationTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_IMAP_HOST");
    private static readonly string? DavUrl = Environment.GetEnvironmentVariable("NERUNA_TEST_DAV_URL");

    private static MailProviderConfig Config(int imapPort = 3143, bool calendar = true) => new(
        "example.com", null,
        [new MailServerSettings(ServerProtocol.Imap, Host!, imapPort, SocketSecurity.None, AuthScheme.PasswordCleartext, "anna@example.com")],
        [new MailServerSettings(ServerProtocol.Smtp, Host!, 3025, SocketSecurity.None, AuthScheme.PasswordCleartext, "anna@example.com")],
        calendar ? [new DavServerSettings(ServerProtocol.CalDav, new Uri(DavUrl!), "anna@example.com")] : []);

    [Fact]
    public async Task Edited_accounts_keep_their_data_and_fail_safely()
    {
        Assert.SkipWhen(Host is null || DavUrl is null, "NERUNA_TEST_IMAP_HOST / NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var registry = new ProviderRegistry(
            [new ImapProviderFactory(credentials, NullLoggerFactory.Instance)],
            [new CalDavProviderFactory(http, credentials, NullLoggerFactory.Instance)],
            []);
        var setup = new AccountSetupService(registry, env.Accounts, credentials);

        var created = setup.BuildAccount("Anna Muster", "anna@example.com", Config());
        await setup.CreateAsync(created, "geheim", ct);
        var mailId = created.ConnectionsOf(ServiceKind.Mail).Single().Id;
        var calendarId = created.ConnectionsOf(ServiceKind.Calendar).Single().Id;
        var controller = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);
        await controller.SyncConnectionAsync(created.ConnectionsOf(ServiceKind.Mail).Single(), ct);
        var folders = (await controller.GetFoldersAsync(mailId, ct)).Count;
        Assert.True(folders > 0);

        // The form shows what is stored; names change, connections (and their offline data) stay.
        var described = setup.Describe(created);
        Assert.Equal((Host, 3143, "anna@example.com"), (described.PreferredIncoming!.Host, described.PreferredIncoming.Port, described.PreferredIncoming.UsernameTemplate));
        Assert.Equal(new Uri(DavUrl!), described.CalDav!.ServerUrl);
        var renamed = await setup.UpdateAsync(created, "Anna M.", "Privat", "anna@example.com", Config(), password: null, ct);
        Assert.Equal(("Privat", "Privat", "Anna M."), (renamed.Label, renamed.Title, renamed.DisplayName));
        Assert.Equal(new[] { mailId, calendarId }.Order(), renamed.Connections.Select(c => c.Id).Order());
        Assert.Equal("geheim", await credentials.GetSecretAsync(mailId, ct));
        Assert.Equal(folders, (await controller.GetFoldersAsync(mailId, ct)).Count);

        // A wrong password (or server): nothing changes – not the account, not the stored password.
        await Assert.ThrowsAsync<AccountSetupException>(() => setup.UpdateAsync(renamed, "X", "Kaputt", "anna@example.com", Config(), "falsch", ct));
        await Assert.ThrowsAsync<AccountSetupException>(() => setup.UpdateAsync(renamed, "X", "Kaputt", "anna@example.com", Config(imapPort: 1), null, ct));
        var stored = Assert.Single(await env.Accounts.GetAccountsAsync(ct));
        Assert.Equal("Privat", stored.Label);
        Assert.Equal("3143", stored.ConnectionsOf(ServiceKind.Mail).Single().Settings["imap.port"]);
        Assert.Equal("geheim", await credentials.GetSecretAsync(mailId, ct));
        Assert.Equal("geheim", await credentials.GetSecretAsync(calendarId, ct));

        // Calendar left empty: that connection goes (with its password); added again: a new one, same password.
        var withoutCalendar = await setup.UpdateAsync(stored, "Anna M.", "Privat", "anna@example.com", Config(calendar: false), null, ct);
        Assert.Equal([mailId], withoutCalendar.Connections.Select(c => c.Id));
        Assert.Null(await credentials.GetSecretAsync(calendarId, ct));
        var again = await setup.UpdateAsync(withoutCalendar, "Anna M.", null, "anna@example.com", Config(), null, ct);
        var newCalendar = again.ConnectionsOf(ServiceKind.Calendar).Single();
        Assert.NotEqual(calendarId, newCalendar.Id);
        Assert.Equal("geheim", await credentials.GetSecretAsync(newCalendar.Id, ct));
        Assert.Equal("anna@example.com", again.Title); // no label: the address again
        Assert.Equal(folders, (await controller.GetFoldersAsync(mailId, ct)).Count);
    }
}
