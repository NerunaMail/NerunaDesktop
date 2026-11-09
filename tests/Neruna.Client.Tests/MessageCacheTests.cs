using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

/// <summary>The local copy of a message: fetched by two at once (reading pane and "Bearbeiten"), both get it.</summary>
public class MessageCacheTests
{
    [Fact]
    public async Task The_same_message_fetched_several_times_at_once_comes_to_each()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Deliver("INBOX", "Vertrag", new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);
        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, ct)).Single(f => f.Role == FolderRole.Inbox);
        var summary = Assert.Single(await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct));

        var fetches = Enumerable.Range(0, 8).Select(_ => Task.Run(() => env.Mail.GetMessageAsync(connection, inbox, summary.RemoteId, ct), ct));
        var messages = await Task.WhenAll(fetches);

        Assert.All(messages, m => Assert.Equal("Vertrag", m.Subject));
        Assert.Equal("Vertrag", (await env.Mail.GetMessageAsync(connection, inbox, summary.RemoteId, ct)).Subject); // from the cache now
    }

    [Fact]
    public async Task Storing_the_same_message_twice_at_once_fails_for_neither()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var store = env.Get<Neruna.Core.IMailStore>();
        var connection = Guid.NewGuid();
        var mime = System.Text.Encoding.ASCII.GetBytes("Subject: Vertrag\r\n\r\n" + new string('x', 4_000_000));

        // Large enough that the writes overlap.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var stream = new MemoryStream(mime);
            await store.SaveMessageContentAsync(connection, "INBOX", "42", stream, ct);
        }, ct)));

        await using var stored = await store.OpenMessageContentAsync(connection, "INBOX", "42", ct);
        Assert.Equal(mime.Length, Assert.IsType<FileStream>(stored).Length);
    }
}
