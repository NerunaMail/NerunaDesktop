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

/// <summary>mailto: links from the system (RFC 6068) and blind copies.</summary>
[Collection(Testlab.Name)]
public class MailtoAndBccTests
{
    [Fact]
    public void Mailto_links_become_a_new_message()
    {
        var draft = MailtoLink.Parse("mailto:anna@example.com,beat+news@example.com?cc=carla@example.com&bcc=dora@example.com&Subject=Gr%C3%BCezi%20zusammen&body=Zeile%201%0D%0AZeile%202");
        Assert.Equal("anna@example.com; beat+news@example.com", draft.To);
        Assert.Equal("carla@example.com", draft.Cc);
        Assert.Equal("dora@example.com", draft.Bcc);
        Assert.Equal("Grüezi zusammen", draft.Subject);
        Assert.Equal("Zeile 1\nZeile 2", draft.Body);

        Assert.Equal("info@example.com", MailtoLink.Parse("MAILTO:?to=info%40example.com").To);
        Assert.Equal(string.Empty, MailtoLink.Parse("mailto:").To);
        Assert.True(MailtoLink.IsMailto(" mailto:x@example.com"));
        Assert.False(MailtoLink.IsMailto("C:\\Mails\\x.eml"));
    }

    [Fact]
    public void Bcc_is_kept_in_the_message_and_in_drafts()
    {
        var draft = ComposeDraft.Empty with { To = "anna@example.com", Subject = "x", Body = "y", Bcc = "geheim@example.com" };
        var message = MessageComposer.Build(new MailboxAddress("Ich", "ich@example.com"), draft, []);
        Assert.Equal("geheim@example.com", Assert.Single(message.Bcc.Mailboxes).Address);
        Assert.Equal("geheim@example.com", MessageComposer.FromDraft(message, "1").Bcc);
    }

    [Fact]
    public async Task Bcc_recipients_get_the_mail_without_seeing_the_header()
    {
        var host = Environment.GetEnvironmentVariable("NERUNA_TEST_IMAP_HOST");
        Assert.SkipWhen(host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var controller = new MailController(env.Accounts, env.MailStore, new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []), NullLogger<MailController>.Instance);
        var settings = new ImapSettings(host!, 3143, SocketSecurity.None, host!, 3025, SocketSecurity.None, "anna@example.com");
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "geheim", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", "anna@example.com", [connection]), ct);

        var subject = "Bcc " + Guid.NewGuid().ToString("N")[..8];
        var draft = ComposeDraft.Empty with { To = "someone-else@example.com", Subject = subject, Body = "Blindkopie", Bcc = "anna@example.com" };
        await controller.SendAsync(connection, MessageComposer.Build(new MailboxAddress("Anna", "anna@example.com"), draft, []), ct);

        await controller.SyncAllAsync(ct);
        var inbox = Assert.Single(await controller.GetFoldersAsync(connection.Id, ct), f => f.Role == FolderRole.Inbox);
        var summary = Assert.Single(await controller.GetMessagesAsync(inbox, cancellationToken: ct), m => m.Subject == subject);
        var received = await controller.GetMessageAsync(connection, inbox, summary.RemoteId, ct);
        Assert.Empty(received.Bcc.Mailboxes);
        Assert.Null(received.Headers[HeaderId.Bcc]);
    }
}
