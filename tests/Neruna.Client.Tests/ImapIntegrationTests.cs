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

/// <summary>
/// Runs the real IMAP/SMTP provider against GreenMail. Skipped unless NERUNA_TEST_IMAP_HOST is set, e.g.:
/// <code>
/// docker run -d --rm -p 3025:3025 -p 3143:3143 -e GREENMAIL_OPTS='-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap
///   -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users=anna:geheim@example.com -Dgreenmail.users.login=email' greenmail/standalone:2.1.5
/// NERUNA_TEST_IMAP_HOST=127.0.0.1 dotnet test
/// </code>
/// </summary>
[Collection(Testlab.Name)]
public class ImapIntegrationTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_IMAP_HOST");

    [Fact]
    public async Task Send_sync_read_and_flag_against_real_server()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();

        var credentials = env.Get<ICredentialStore>();
        var factory = new ImapProviderFactory(credentials, NullLoggerFactory.Instance);
        var registry = new ProviderRegistry([factory], [], []);
        var controller = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);

        var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, "anna@example.com");
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "geheim", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", "anna@example.com", [connection]), ct);

        var subject = "Integration " + Guid.NewGuid().ToString("N")[..8];
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "Grüezi aus dem Test" } };
        message.From.Add(new MailboxAddress("Anna", "anna@example.com"));
        message.To.Add(new MailboxAddress("Anna", "anna@example.com"));
        await controller.SendAsync(connection, message, ct);

        var report = await controller.SyncAllAsync(ct);
        Assert.Empty(report.Failures);

        var inbox = Assert.Single(await controller.GetFoldersAsync(connection.Id, ct), f => f.Role == FolderRole.Inbox);
        var summary = Assert.Single(await controller.GetMessagesAsync(inbox, cancellationToken: ct), m => m.Subject == subject);
        Assert.Equal("anna@example.com", summary.From?.Address);
        Assert.False(summary.Flags.HasFlag(MessageFlags.Seen));

        var full = await controller.GetMessageAsync(connection, inbox, summary.RemoteId, ct);
        Assert.Equal("Grüezi aus dem Test", full.TextBody?.Trim());

        await controller.SetFlagsAsync(connection, inbox, [summary.RemoteId], MessageFlags.Seen, add: true, ct);
        await controller.SyncAllAsync(ct);

        var after = Assert.Single(await controller.GetMessagesAsync(inbox, cancellationToken: ct), m => m.Subject == subject);
        Assert.True(after.Flags.HasFlag(MessageFlags.Seen));

        // Answered (\Answered) and forwarded ($Forwarded) come back from the server with the next sync.
        await controller.SetFlagsAsync(connection, inbox, [summary.RemoteId], MessageFlags.Answered | MessageFlags.Forwarded, add: true, ct);
        await env.MailStore.UpdateFlagsAsync(inbox.ConnectionId, inbox.RemoteId, [summary.RemoteId], MessageFlags.Answered | MessageFlags.Forwarded, false, ct);
        await controller.SyncAllAsync(ct);
        var responded = Assert.Single(await controller.GetMessagesAsync(inbox, cancellationToken: ct), m => m.Subject == subject);
        Assert.True(responded.Flags.HasFlag(MessageFlags.Answered));
        Assert.True(responded.Flags.HasFlag(MessageFlags.Forwarded));
    }

    [Fact]
    public async Task Messages_move_to_another_account_with_flags_and_content()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();

        var credentials = env.Get<ICredentialStore>();
        var registry = new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []);
        var controller = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);

        async Task<ServiceConnection> AccountAsync(string address)
        {
            var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, address);
            var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
            await credentials.SetSecretAsync(connection.Id, "geheim", ct);
            await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), address, address, [connection]), ct);
            return connection;
        }

        var anna = await AccountAsync("anna@example.com");
        var lea = await AccountAsync("lea@example.com");

        // Two messages for Anna; one of them read and flagged.
        var tag = Guid.NewGuid().ToString("N")[..8];
        foreach (var n in new[] { 1, 2 })
        {
            var message = new MimeMessage { Subject = $"Umzug {tag} #{n}", Body = new TextPart("plain") { Text = $"Inhalt {n}" } };
            message.From.Add(new MailboxAddress("Anna", "anna@example.com"));
            message.To.Add(new MailboxAddress("Anna", "anna@example.com"));
            await controller.SendAsync(anna, message, ct);
        }

        await controller.SyncAllAsync(ct);
        var annaInbox = Assert.Single(await controller.GetFoldersAsync(anna.Id, ct), f => f.Role == FolderRole.Inbox);
        var leaInbox = Assert.Single(await controller.GetFoldersAsync(lea.Id, ct), f => f.Role == FolderRole.Inbox);
        var moving = (await controller.GetMessagesAsync(annaInbox, cancellationToken: ct)).Where(m => m.Subject.Contains(tag, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, moving.Count);
        var first = moving.Single(m => m.Subject.EndsWith("#1", StringComparison.Ordinal));
        await controller.SetFlagsAsync(anna, annaInbox, [first.RemoteId], MessageFlags.Seen | MessageFlags.Flagged, add: true, ct);
        moving = (await controller.GetMessagesAsync(annaInbox, cancellationToken: ct)).Where(m => m.Subject.Contains(tag, StringComparison.Ordinal)).ToList();

        var moved = await controller.MoveToAccountAsync(anna, annaInbox, moving, lea, leaInbox, ct);

        Assert.Equal(2, moved);
        await controller.SyncAllAsync(ct);
        Assert.DoesNotContain(await controller.GetMessagesAsync(annaInbox, cancellationToken: ct), m => m.Subject.Contains(tag, StringComparison.Ordinal));
        var arrived = (await controller.GetMessagesAsync(leaInbox, cancellationToken: ct)).Where(m => m.Subject.Contains(tag, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, arrived.Count);
        var flagged = arrived.Single(m => m.Subject.EndsWith("#1", StringComparison.Ordinal));
        Assert.True(flagged.Flags.HasFlag(MessageFlags.Seen) && flagged.Flags.HasFlag(MessageFlags.Flagged));
        Assert.False(arrived.Single(m => m.Subject.EndsWith("#2", StringComparison.Ordinal)).Flags.HasFlag(MessageFlags.Seen));
        Assert.Equal("Inhalt 1", (await controller.GetMessageAsync(lea, leaInbox, flagged.RemoteId, ct)).TextBody?.Trim());
    }

    [Fact]
    public async Task Messages_marked_deleted_elsewhere_disappear_and_come_back_when_restored()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();

        var credentials = env.Get<ICredentialStore>();
        var registry = new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []);
        var controller = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);
        var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, "marco@example.com");
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "geheim", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Marco", "marco@example.com", [connection]), ct);

        // Another program works on the mailbox: an own, fresh folder, so the message is the only (and oldest) one there –
        // the case that once kept a restored message away for good.
        var folderName = "Wiederherstellen-" + Guid.NewGuid().ToString("N")[..8];
        const string subject = "Woanders gelöscht";
        async Task OtherProgramAsync(Func<MailKit.IMailFolder, Task> action)
        {
            using var imap = new MailKit.Net.Imap.ImapClient();
            await imap.ConnectAsync(Host!, 3143, MailKit.Security.SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync("marco@example.com", "geheim", ct);
            var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
            var folder = (await personal.GetSubfoldersAsync(false, ct)).FirstOrDefault(f => f.Name == folderName)
                         ?? await personal.CreateAsync(folderName, true, ct)
                         ?? throw new InvalidOperationException("Folder not created");
            await action(folder);
            await imap.DisconnectAsync(true, ct);
        }

        async Task SetDeletedAsync(bool deleted) => await OtherProgramAsync(async folder =>
        {
            await folder.OpenAsync(MailKit.FolderAccess.ReadWrite, ct);
            var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, ct);
            var request = new MailKit.StoreFlagsRequest(deleted ? MailKit.StoreAction.Add : MailKit.StoreAction.Remove, MailKit.MessageFlags.Deleted) { Silent = true };
            await folder.StoreAsync(uids, request, ct);
        });

        await OtherProgramAsync(async folder =>
        {
            await folder.SubscribeAsync(ct);
            var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "x" } };
            message.From.Add(new MailboxAddress("Marco", "marco@example.com"));
            await folder.AppendAsync(new MailKit.AppendRequest(message), ct);
        });

        async Task<IReadOnlyList<string>> SubjectsAsync()
        {
            Assert.Empty((await controller.SyncAllAsync(ct)).Failures);
            var folder = Assert.Single(await controller.GetFoldersAsync(connection.Id, ct), f => f.Name == folderName);
            return [.. (await controller.GetMessagesAsync(folder, cancellationToken: ct)).Select(m => m.Subject)];
        }

        Assert.Equal([subject], await SubjectsAsync());

        // Marked \Deleted without expunging (deferred purge): gone in Neruna …
        await SetDeletedAsync(true);
        Assert.Empty(await SubjectsAsync());

        // … and back once restored – also as the only and oldest message of the folder.
        await SetDeletedAsync(false);
        Assert.Equal([subject], await SubjectsAsync());
    }

    [Fact]
    public async Task Only_subscribed_folders_are_shown_and_hidden_ones_leave_with_their_mail()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var registry = new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []);
        var controller = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);
        var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, "lea@example.com");
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "geheim", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Lea", "lea@example.com", [connection]), ct);

        // Two folders made by another program.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        using (var imap = new MailKit.Net.Imap.ImapClient())
        {
            await imap.ConnectAsync(Host!, 3143, MailKit.Security.SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync("lea@example.com", "geheim", ct);
            var root = imap.GetFolder(imap.PersonalNamespaces[0]);
            // Subscribed, as other programs do when they create one (the server may already use subscriptions).
            await (await root.CreateAsync("Kunden" + suffix, true, ct))!.SubscribeAsync(ct);
            await (await root.CreateAsync("Alt" + suffix, true, ct))!.SubscribeAsync(ct);
            await imap.DisconnectAsync(true, ct);
        }

        var all = await controller.GetSubscriptionsAsync(connection, ct);
        var keep = all.Single(s => s.Folder.Name == "Kunden" + suffix).Folder.RemoteId;
        var hide = all.Single(s => s.Folder.Name == "Alt" + suffix).Folder.RemoteId;
        Assert.True(all.Single(s => s.Folder.Role == FolderRole.Inbox).Required);
        await controller.SyncConnectionAsync(connection, ct);
        Assert.Contains(await controller.GetFoldersAsync(connection.Id, ct), f => f.RemoteId == hide);

        try
        {
            // Everything but "Alt…": it disappears here (with its cache) and is unsubscribed on the server.
            await controller.SetSubscriptionsAsync(connection, [.. all.Select(s => s.Folder.RemoteId).Where(id => id != hide)], ct);
            var shown = await controller.GetFoldersAsync(connection.Id, ct);
            Assert.Contains(shown, f => f.RemoteId == keep);
            Assert.Contains(shown, f => f.Role == FolderRole.Inbox);
            Assert.DoesNotContain(shown, f => f.RemoteId == hide);
            Assert.False((await controller.GetSubscriptionsAsync(connection, ct)).Single(s => s.Folder.RemoteId == hide).Subscribed);

            // The inbox cannot be hidden.
            await controller.SetSubscriptionsAsync(connection, [keep], ct);
            Assert.Contains(await controller.GetFoldersAsync(connection.Id, ct), f => f.Role == FolderRole.Inbox);
        }
        finally
        {
            await controller.SetSubscriptionsAsync(connection, [.. (await controller.GetSubscriptionsAsync(connection, ct)).Select(s => s.Folder.RemoteId)], ct);
        }
    }
}
