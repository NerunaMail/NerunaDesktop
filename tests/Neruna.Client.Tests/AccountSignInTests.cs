using Neruna.Core.Accounts;
using Neruna.Core.Auth;
using Neruna.Core.Providers;
using Neruna.Providers.Dav;
using Neruna.Providers.Ics;
using Neruna.Providers.Imap;
using Microsoft.Extensions.Logging.Abstractions;

namespace Neruna.Client.Tests;

/// <summary>Account setup without knowing the protocols: sign-in providers and one-line server descriptions.</summary>
public class AccountSignInTests
{
    private sealed class FakeSignIn(bool available) : IAccountSignIn
    {
        public string ProviderId => FakeMailProviderFactory.Id;

        public bool IsAvailable => available;

        public Account? SignedInAgain { get; private set; }

        public Task<SignedInAccount> SignInAsync(Account? existing, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
        {
            SignedInAgain = existing;
            var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, FakeMailProviderFactory.Id, new Dictionary<string, string>());
            return Task.FromResult(new SignedInAccount(loginHint ?? "eva@example.com", "Eva Muster", existing?.Connections ?? [mail]));
        }
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Accounts_are_set_up_by_signing_in_without_the_ui_knowing_the_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var signIn = new FakeSignIn(available: true);
        var setup = new AccountSetupService(env.Get<ProviderRegistry>(), env.Accounts, env.Get<Neruna.Core.Security.ICredentialStore>(), [signIn]);

        Assert.True(setup.CanSignIn(FakeMailProviderFactory.Id));
        var account = await setup.CreateBySignInAsync(FakeMailProviderFactory.Id, null, " Privat ", null, new NoBrowser(), ct);
        Assert.Equal(("Eva Muster", "eva@example.com", "Privat"), (account.DisplayName, account.EmailAddress, account.Label));
        Assert.True(setup.IsSignedIn(account));
        Assert.Single(await env.Accounts.GetAccountsAsync(ct));

        // Signing in again keeps the account (and its connections), the names are taken over.
        var renamed = await setup.SignInAgainAsync(account, "Eva M.", null, null, new NoBrowser(), ct);
        Assert.Same(account, signIn.SignedInAgain);
        Assert.Equal(account.Connections, renamed.Connections);
        Assert.Equal("Eva M.", Assert.Single(await env.Accounts.GetAccountsAsync(ct)).DisplayName);

        // Without the app registration the choice is off.
        var off = new AccountSetupService(env.Get<ProviderRegistry>(), env.Accounts, env.Get<Neruna.Core.Security.ICredentialStore>(), [new FakeSignIn(available: false)]);
        Assert.False(off.CanSignIn(FakeMailProviderFactory.Id));
        await Assert.ThrowsAsync<AccountSetupException>(() => off.CreateBySignInAsync(FakeMailProviderFactory.Id, null, null, null, new NoBrowser(), ct));
    }

    [Fact]
    public void Each_provider_describes_its_server_in_one_line()
    {
        using var http = new HttpClient();
        var registry = new ProviderRegistry(
            [new ImapProviderFactory(new Neruna.Storage.Credentials.LocalCredentialStore(Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"))), NullLoggerFactory.Instance)],
            [new CalDavProviderFactory(http, null!, NullLoggerFactory.Instance), new IcsProviderFactory(http)],
            [new CardDavProviderFactory(http, null!, NullLoggerFactory.Instance)]);

        var imap = new ImapSettings("imap.example.ch", 993, Neruna.Contracts.Discovery.SocketSecurity.SslOnConnect, "smtp.example.ch", 465, Neruna.Contracts.Discovery.SocketSecurity.SslOnConnect, "anna");
        Assert.Equal("imap.example.ch:993 · SMTP smtp.example.ch:465 · anna",
            registry.Summary(new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, imap.ToDictionary())));

        var dav = new DavSettings(new Uri("https://dav.example.ch/"), "anna").ToDictionary();
        Assert.Equal("https://dav.example.ch/ · anna", registry.Summary(new ServiceConnection(Guid.NewGuid(), ServiceKind.Contacts, ProviderIds.CardDav, dav)));

        var ics = new Dictionary<string, string> { [IcsCalendarProvider.UrlSetting] = "https://example.ch/feed.ics" };
        Assert.Equal("https://example.ch/feed.ics", registry.Summary(new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.Ics, ics)));

        // Broken or unknown: no line rather than a broken account list.
        Assert.Equal(string.Empty, registry.Summary(new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, new Dictionary<string, string>())));
        Assert.Equal(string.Empty, registry.Summary(new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, "unknown", new Dictionary<string, string>())));
    }
}
