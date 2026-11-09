using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

/// <summary>Ctrl+Z after deleting or moving: the messages go back, found by Message-ID under their new ids.</summary>
public class MailUndoTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_message_deleted_into_the_trash_comes_back_to_the_inbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Folders["Trash"] = [];
        var message = env.MailServer.Deliver("INBOX", "aus Versehen gelöscht", Now);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);
        var folders = await env.Mail.GetFoldersAsync(connection.Id, ct);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var trash = folders.Single(f => f.Role == FolderRole.Trash);
        var summary = Assert.Single(await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct));

        await env.Mail.DeleteAsync(connection, inbox, [summary.RemoteId], ct);
        Assert.NotEqual(message.RemoteId, Assert.Single(env.MailServer.Folders["Trash"]).RemoteId); // a new id there

        Assert.Equal(1, await env.Mail.MoveBackAsync(connection, trash, [summary], connection, inbox, ct));

        Assert.Empty(env.MailServer.Folders["Trash"]);
        Assert.Equal("aus Versehen gelöscht", Assert.Single(env.MailServer.Folders["INBOX"]).Subject);
        Assert.Empty(await env.Mail.GetMessagesAsync(trash, cancellationToken: ct));
        Assert.Single(await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct));
    }

    [Fact]
    public async Task Only_the_moved_messages_go_back_and_one_gone_meanwhile_is_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Folders["Projekte"] = [];
        env.MailServer.Deliver("Projekte", "war schon da", Now.AddDays(-1));
        env.MailServer.Deliver("INBOX", "eins", Now);
        env.MailServer.Deliver("INBOX", "zwei", Now);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);
        var folders = await env.Mail.GetFoldersAsync(connection.Id, ct);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var projects = folders.Single(f => f.RemoteId == "Projekte");
        var moved = await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct);

        await env.Mail.MoveAsync(connection, inbox, [.. moved.Select(m => m.RemoteId)], projects, ct);
        env.MailServer.Folders["Projekte"].RemoveAll(m => m.Subject == "zwei"); // deleted elsewhere in the meantime

        Assert.Equal(1, await env.Mail.MoveBackAsync(connection, projects, moved, connection, inbox, ct));

        Assert.Equal(["eins"], env.MailServer.Folders["INBOX"].Select(m => m.Subject));
        Assert.Equal(["war schon da"], env.MailServer.Folders["Projekte"].Select(m => m.Subject));
    }
}
