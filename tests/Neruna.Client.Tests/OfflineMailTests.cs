using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public sealed class OfflineMailTests
{
    [Fact]
    public async Task Mail_within_the_period_is_kept_complete_older_leaves_the_device()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var recent = env.MailServer.Deliver("INBOX", "neu", now.AddDays(-2));
        var old = env.MailServer.Deliver("INBOX", "alt", now.AddDays(-60));
        env.MailServer.Folders["Trash"] = [];
        var trashed = env.MailServer.Deliver("Trash", "gelöscht", now.AddDays(-1));
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        var account = (await env.Accounts.GetAccountsAsync(ct)).Single();

        async Task<bool> Cached(MessageSummary m, string folder = "INBOX")
        {
            await using var content = await env.MailStore.OpenMessageContentAsync(connection.Id, folder, m.RemoteId, ct);
            return content is not null;
        }

        // Default: one month – the recent mail is fetched ahead, the old one and the trash are not.
        await env.Mail.SyncAllAsync(ct);
        Assert.True(await Cached(recent));
        Assert.False(await Cached(old));
        Assert.False(await Cached(trashed, "Trash"));

        // A year: the old one too.
        await env.Mail.SetOfflinePeriodAsync(account.Id, OfflinePeriod.Year, ct);
        await env.Mail.SyncAllAsync(ct);
        Assert.True(await Cached(old));

        // A week: the old content leaves the device, the message stays in the list.
        await env.Mail.SetOfflinePeriodAsync(account.Id, OfflinePeriod.Week, ct);
        await env.Mail.SyncAllAsync(ct);
        Assert.False(await Cached(old));
        Assert.True(await Cached(recent));
        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, ct)).Single(f => f.RemoteId == "INBOX");
        Assert.Equal(2, (await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct)).Count);

        // Only opened ones: nothing more is fetched.
        await env.Mail.SetOfflinePeriodAsync(account.Id, OfflinePeriod.OpenedOnly, ct);
        var downloads = env.MailServer.MessageDownloads;
        env.MailServer.Deliver("INBOX", "noch neuer", now);
        await env.Mail.SyncAllAsync(ct);
        Assert.Equal(downloads, env.MailServer.MessageDownloads);
    }

    [Fact]
    public async Task Mail_in_a_local_file_is_not_copied_ahead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.ExtraCapabilities = MailProviderCapabilities.LocalContent;
        env.MailServer.Deliver("INBOX", "neu", DateTimeOffset.UtcNow.AddDays(-1));
        await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);

        await env.Mail.SyncAllAsync(ct);

        Assert.Equal(0, env.MailServer.MessageDownloads);
    }
}
