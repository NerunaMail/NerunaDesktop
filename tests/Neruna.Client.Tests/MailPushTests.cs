using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

[Collection(Testlab.Name)]
public class MailPushTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_IMAP_HOST");

    [Fact]
    public async Task New_mail_is_synced_and_reported_when_the_server_signals_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Deliver("INBOX", "schon da", DateTimeOffset.UtcNow.AddHours(-1));
        await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);

        var push = env.Get<MailPushService>();
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<NewMailEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        push.FolderSynced += (_, _) => synced.TrySetResult();
        push.NewMail += (_, e) => reported.TrySetResult(e);
        await push.StartAsync(ct);
        try
        {
            // The catch-up sync on start reports nothing: no new mail yet.
            await synced.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.False(reported.Task.IsCompleted);

            env.MailServer.Deliver("INBOX", "Neu: Offerte", DateTimeOffset.UtcNow);
            env.MailServer.Deliver("INBOX", "Neu, aber gelesen", DateTimeOffset.UtcNow, MessageFlags.Seen);
            var e = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            Assert.Equal("Neu: Offerte", Assert.Single(e.Messages).Subject);
            Assert.Equal(FolderRole.Inbox, e.Folder.Role);
            var inbox = (await env.Mail.GetFoldersAsync(e.Connection.Id, ct)).Single(f => f.Role == FolderRole.Inbox);
            Assert.Equal(3, (await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct)).Count);
        }
        finally
        {
            await push.StopAsync();
        }
    }

    [Fact]
    public async Task New_mail_is_reported_even_when_another_sync_stored_it_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);

        var push = env.Get<MailPushService>();
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<NewMailEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        push.FolderSynced += (_, _) => synced.TrySetResult();
        push.NewMail += (_, e) => reported.TrySetResult(e);
        await push.StartAsync(ct);
        try
        {
            await synced.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            // The periodic sync stores the message before the push watch looks.
            var message = env.MailServer.Deliver("INBOX", "zuerst vom Abgleich gesehen", DateTimeOffset.UtcNow);
            await env.Mail.SyncAllAsync(ct);

            var e = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(message.RemoteId, Assert.Single(e.Messages).RemoteId);
        }
        finally
        {
            await push.StopAsync();
        }
    }

    [Fact]
    public async Task Imap_idle_reports_a_new_mail_to_oneself_within_seconds()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var registry = new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []);
        var mail = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);
        var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, "lea@example.com");
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "geheim", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Lea", "lea@example.com", [connection]), ct);
        await mail.SyncAllAsync(ct);

        await using var push = new MailPushService(mail, env.MailStore, env.Accounts, registry, NullLogger<MailPushService>.Instance);
        var subject = "IDLE " + Guid.NewGuid().ToString("N")[..8];
        var reported = new TaskCompletionSource<NewMailEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        push.NewMail += (_, e) =>
        {
            if (e.Messages.Any(m => m.Subject == subject))
            {
                reported.TrySetResult(e);
            }
        };
        await push.StartAsync(ct);
        await Task.Delay(1500, ct); // connected and idling

        // A mail to oneself – the usual way to try it out – is reported too.
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "sofort da?" } };
        message.From.Add(new MailboxAddress("Lea", "lea@example.com"));
        message.To.Add(new MailboxAddress("Lea", "lea@example.com"));
        var started = DateTimeOffset.UtcNow;
        await mail.SendAsync(connection, message, ct);

        var e = await reported.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal("lea@example.com", e.Account.EmailAddress);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10), "IDLE should report within seconds, not by polling");
    }
}
