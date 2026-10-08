using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class MailControllerTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sync_stores_folders_and_messages_newest_first()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Folders["Archiv"] = [];
        env.MailServer.Deliver("INBOX", "alt", Monday);
        env.MailServer.Deliver("INBOX", "neu", Monday.AddHours(2), MessageFlags.Seen);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);

        var report = await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.SucceededConnections);
        var folders = await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken);
        Assert.Equal(["INBOX", "Archiv"], folders.Select(f => f.RemoteId));
        var inbox = folders[0];
        Assert.Equal((2, 1), (inbox.TotalCount, inbox.UnreadCount));
        var messages = await env.Mail.GetMessagesAsync(inbox, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["neu", "alt"], messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task Incremental_sync_applies_new_removed_and_flag_changes()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var keep = env.MailServer.Deliver("INBOX", "bleibt", Monday);
        var gone = env.MailServer.Deliver("INBOX", "wird gelöscht", Monday.AddHours(1));
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        env.MailServer.Folders["INBOX"].Remove(gone);
        env.MailServer.SetFlags("INBOX", keep.RemoteId, MessageFlags.Seen | MessageFlags.Flagged);
        env.MailServer.Deliver("INBOX", "neu", Monday.AddHours(3));
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken))[0];
        var messages = await env.Mail.GetMessagesAsync(inbox, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["neu", "bleibt"], messages.Select(m => m.Subject));
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, messages[1].Flags);
    }

    [Fact]
    public async Task Reset_sync_state_replaces_folder_contents()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var old = env.MailServer.Deliver("INBOX", "vorher", Monday);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        env.MailServer.Folders["INBOX"].Remove(old);
        env.MailServer.Deliver("INBOX", "nachher", Monday);
        env.MailServer.Validity = 2;
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken))[0];
        var messages = await env.Mail.GetMessagesAsync(inbox, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["nachher"], messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task Vanished_folder_is_removed_locally()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Folders["Alt"] = [];
        env.MailServer.Deliver("Alt", "x", Monday);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        env.MailServer.Folders.Remove("Alt");
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        var folders = await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken);
        Assert.Equal(["INBOX"], folders.Select(f => f.RemoteId));
    }

    [Fact]
    public async Task Message_content_is_downloaded_once_then_served_offline()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var summary = env.MailServer.Deliver("INBOX", "Offline", Monday);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);
        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken))[0];

        var first = await env.Mail.GetMessageAsync(connection, inbox, summary.RemoteId, TestContext.Current.CancellationToken);
        var second = await env.Mail.GetMessageAsync(connection, inbox, summary.RemoteId, TestContext.Current.CancellationToken);

        Assert.Equal(1, env.MailServer.MessageDownloads);
        Assert.Equal("Hallo Offline", second.TextBody?.Trim());
        Assert.Equal(first.Subject, second.Subject);
    }

    [Fact]
    public async Task Marking_read_updates_server_store_and_unread_badge()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var a = env.MailServer.Deliver("INBOX", "a", Monday);
        env.MailServer.Deliver("INBOX", "b", Monday);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);
        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken))[0];

        await env.Mail.SetFlagsAsync(connection, inbox, [a.RemoteId], MessageFlags.Seen, add: true, TestContext.Current.CancellationToken);
        // Marking twice must not decrement the badge twice.
        await env.Mail.SetFlagsAsync(connection, inbox, [a.RemoteId], MessageFlags.Seen, add: true, TestContext.Current.CancellationToken);

        Assert.True(env.MailServer.Folders["INBOX"][0].Flags.HasFlag(MessageFlags.Seen));
        inbox = (await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken))[0];
        Assert.Equal(1, inbox.UnreadCount);
    }

    [Fact]
    public async Task Connection_with_unavailable_provider_is_skipped_not_failed()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountAsync(ServiceKind.Mail, "ews");

        var report = await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Single(report.Skipped);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task Deleting_account_removes_cached_data()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var summary = env.MailServer.Deliver("INBOX", "weg", Monday);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);
        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken))[0];
        await env.Mail.GetMessageAsync(connection, inbox, summary.RemoteId, TestContext.Current.CancellationToken);

        var account = Assert.Single(await env.Accounts.GetAccountsAsync(TestContext.Current.CancellationToken));
        await env.Accounts.DeleteAccountAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.Empty(await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(env.Directory, "messages", connection.Id.ToString("N"))));
    }
}
