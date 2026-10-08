using Neruna.Core.Accounts;

namespace Neruna.Client.Tests;

public class AccountOrderTests
{
    [Fact]
    public async Task Accounts_keep_the_order_the_user_chose()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var info = new Account(Guid.NewGuid(), "Info", "info@example.com", []);
        var personal = new Account(Guid.NewGuid(), "Persönlich", "patrik@example.com", []);
        await env.Accounts.SaveAccountAsync(info, ct);
        await env.Accounts.SaveAccountAsync(personal, ct);

        // New accounts go to the end.
        Assert.Equal(["Info", "Persönlich"], (await env.Accounts.GetAccountsAsync(ct)).Select(a => a.DisplayName));

        await env.Accounts.SetOrderAsync([personal.Id, info.Id], ct);
        Assert.Equal(["Persönlich", "Info"], (await env.Accounts.GetAccountsAsync(ct)).Select(a => a.DisplayName));

        // Saving an account (e.g. after editing) does not move it; a third one is appended.
        await env.Accounts.SaveAccountAsync(info with { DisplayName = "Info (Firma)" }, ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Archiv", null, []), ct);
        Assert.Equal(["Persönlich", "Info (Firma)", "Archiv"], (await env.Accounts.GetAccountsAsync(ct)).Select(a => a.DisplayName));
    }
}
