using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Discovery;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Ics;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

public class AccountSetupTests
{
    private static string Fixture(string name) => ContractFixtures.Read(name);

    [Fact]
    public void Registered_providers_claim_their_part_of_the_discovered_config()
    {
        // IMAP is registered, CalDAV/CardDAV are not yet: the account gets mail only.
        var registry = new ProviderRegistry(
            [new ImapProviderFactory(new NullCredentialStore(), NullLoggerFactory.Instance)],
            [new IcsProviderFactory(new HttpClient())],
            []);
        var setup = new AccountSetupService(registry, null!, null!);

        var account = setup.BuildAccount("Anna Muster", "anna.muster@example.com", AutoconfigXml.Parse(Fixture("autoconfig-example.xml")));

        var mail = Assert.Single(account.Connections);
        Assert.Equal((ServiceKind.Mail, ProviderIds.Imap), (mail.Kind, mail.ProviderId));
        Assert.Equal(
            new ImapSettings("mail.example.com", 993, SocketSecurity.SslOnConnect, "mail.example.com", 587, SocketSecurity.StartTls, "anna.muster@example.com"),
            ImapSettings.FromDictionary(mail.Settings));
    }

    [Fact]
    public async Task Failing_connection_test_leaves_nothing_behind()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var calendar = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.Ics, new Dictionary<string, string> { [IcsCalendarProvider.UrlSetting] = "https://down.example.com/x.ics" });
        var account = new Account(Guid.NewGuid(), "Kaputt", null, [calendar]);

        await Assert.ThrowsAsync<AccountSetupException>(() => env.Get<AccountSetupService>().CreateAsync(account, "pw", TestContext.Current.CancellationToken));

        Assert.Empty(await env.Accounts.GetAccountsAsync(TestContext.Current.CancellationToken));
        Assert.Null(await credentials.GetSecretAsync(calendar.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Successful_setup_stores_account_and_secret()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, FakeMailProviderFactory.Id, new Dictionary<string, string>());
        var account = new Account(Guid.NewGuid(), "Anna", "anna@example.com", [mail]);

        await env.Get<AccountSetupService>().CreateAsync(account, "geheim", TestContext.Current.CancellationToken);

        Assert.Single(await env.Accounts.GetAccountsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("geheim", await env.Get<ICredentialStore>().GetSecretAsync(mail.Id, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("geheim", await File.ReadAllTextAsync(Path.Combine(env.Directory, "credentials.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_prefers_organization_server_then_falls_back_to_autoconfig()
    {
        var http = new StubHttpHandler();
        http.Responses["https://neruna.example/api/v1/discovery?email=anna%40example.com"] = (HttpStatusCode.OK, Fixture("discovery-example.json"));
        http.Responses["https://example.com/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress=anna%40example.com"] = (HttpStatusCode.OK, Fixture("autoconfig-example.xml"));
        var discovery = new AccountDiscovery(new HttpClient(http), NullLogger<AccountDiscovery>.Instance);

        var withoutOrg = await discovery.DiscoverAsync("anna@example.com", TestContext.Current.CancellationToken);
        Assert.Equal("example.com", withoutOrg?.Source);
        Assert.Equal("https://autoconfig.example.com/mail/config-v1.1.xml?emailaddress=anna%40example.com", http.Requested[0]);

        discovery.OrganizationServer = new Uri("https://neruna.example/");
        var withOrg = await discovery.DiscoverAsync("anna@example.com", TestContext.Current.CancellationToken);
        Assert.Equal("Example AG", withOrg?.OrganizationName);
        Assert.Equal("mail.example.com", withOrg?.Config.PreferredIncoming?.Host);
    }

    [Fact]
    public async Task Discovery_returns_null_when_nothing_answers()
    {
        var discovery = new AccountDiscovery(new HttpClient(new StubHttpHandler()), NullLogger<AccountDiscovery>.Instance);

        Assert.Null(await discovery.DiscoverAsync("anna@nowhere.example", TestContext.Current.CancellationToken));
    }

    private sealed class NullCredentialStore : ICredentialStore
    {
        public string Location => "nirgends";

        public Task<string?> GetSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSecretAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
