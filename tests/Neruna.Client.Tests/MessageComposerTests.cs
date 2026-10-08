using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class MessageComposerTests
{
    private static MimeMessage Original()
    {
        var message = new MimeMessage { Subject = "AW: Re: Offerte", MessageId = "orig@example.com", Date = new DateTimeOffset(2026, 10, 7, 9, 30, 0, TimeSpan.FromHours(2)) };
        message.From.Add(new MailboxAddress("Marco", "marco@example.com"));
        message.ReplyTo.Add(new MailboxAddress("Verkauf", "verkauf@example.com"));
        message.To.Add(new MailboxAddress("Anna", "anna@example.com"));
        message.To.Add(new MailboxAddress("Lea", "lea@example.com"));
        message.Cc.Add(new MailboxAddress("Thomas", "thomas@example.com"));
        message.References.Add("first@example.com");

        var body = new BodyBuilder { TextBody = "Anbei die Offerte." };
        body.Attachments.Add("offerte.pdf", [1, 2, 3], new ContentType("application", "pdf"));
        message.Body = body.ToMessageBody();
        return message;
    }

    [Fact]
    public void Reply_goes_to_reply_to_with_single_prefix_and_threading()
    {
        var draft = MessageComposer.Reply(Original(), "anna@example.com", replyAll: false);

        Assert.Equal("Verkauf <verkauf@example.com>", draft.To);
        Assert.Equal(string.Empty, draft.Cc);
        Assert.Equal("AW: Offerte", draft.Subject);
        Assert.Equal("orig@example.com", draft.InReplyTo);
        Assert.Equal(["first@example.com", "orig@example.com"], draft.References);
        Assert.Contains("-----Ursprüngliche Nachricht-----", draft.Body, StringComparison.Ordinal);
        Assert.Contains("Anbei die Offerte.", draft.Body, StringComparison.Ordinal);
        Assert.Empty(draft.Attachments);
    }

    [Fact]
    public void Names_are_quoted_only_when_needed()
    {
        var original = Original();
        original.ReplyTo.Clear();
        original.From.Clear();
        original.From.Add(new MailboxAddress("Muster, Anna", "a@example.com"));

        var draft = MessageComposer.Reply(original, "me@example.com", replyAll: false);

        Assert.Equal("\"Muster, Anna\" <a@example.com>", draft.To);
        Assert.Equal("a@example.com", InternetAddressList.Parse(draft.To).Mailboxes.Single().Address);
    }

    [Fact]
    public void Reply_all_copies_everyone_except_me()
    {
        var draft = MessageComposer.Reply(Original(), "ANNA@example.com", replyAll: true);

        Assert.Contains("lea@example.com", draft.Cc, StringComparison.Ordinal);
        Assert.Contains("thomas@example.com", draft.Cc, StringComparison.Ordinal);
        Assert.DoesNotContain("anna@example.com", draft.Cc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forward_keeps_attachments_and_builds_valid_message()
    {
        var draft = MessageComposer.Forward(Original()) with { To = "jonas@example.com" };

        Assert.Equal("WG: Offerte", draft.Subject);
        Assert.Single(draft.Attachments);

        var built = MessageComposer.Build(new MailboxAddress("Anna", "anna@example.com"), draft, []);
        Assert.Equal("offerte.pdf", Assert.Single(built.Attachments).ContentDisposition?.FileName);
        Assert.Equal("jonas@example.com", built.To.Mailboxes.Single().Address);
    }

    [Fact]
    public void Html_reply_keeps_original_formatting_and_inline_images_under_a_reply_header()
    {
        var original = Original();
        var builder = new BodyBuilder { TextBody = "Text", HtmlBody = "<p><b>Fett</b> &amp; <span style=\"color:#c50f1f\">rot</span></p><img src=\"cid:logo@x\">" };
        var logo = builder.LinkedResources.Add("logo.png", [137, 80, 78, 71], new ContentType("image", "png"));
        logo.ContentId = "logo@x";
        original.Body = builder.ToMessageBody();

        var draft = MessageComposer.Reply(original, "anna@example.com", replyAll: false);

        Assert.NotNull(draft.HtmlBody);
        Assert.Contains("<b>Von:</b> Marco &lt;marco@example.com&gt;", draft.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<b>Betreff:</b> AW: Re: Offerte", draft.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<b>Fett</b>", draft.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("color:#c50f1f", draft.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", draft.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", draft.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_body_is_sent_with_text_alternative_and_embedded_images()
    {
        var draft = ComposeDraft.Empty with
        {
            To = "lea@example.com",
            Subject = "Formatiert",
            HtmlBody = "<div style=\"font-family: Georgia; font-size: 12pt\"><b>Hallo</b> Lea<br><img src=\"data:image/png;base64,iVBORw0KGgo=\"></div>",
        };

        var message = MessageComposer.Build(new MailboxAddress("Anna", "anna@example.com"), draft, []);

        Assert.Contains("<b>Hallo</b>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("src=\"cid:", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("Hallo Lea", message.TextBody?.Trim());
        var image = Assert.Single(message.BodyParts.OfType<MimePart>(), p => p.ContentType.MediaType == "image");
        Assert.NotNull(image.ContentId);
    }

    [Theory]
    [InlineData("Re: AW: Fwd: WG: Test", "Test")]
    [InlineData("RE[2]: Test", "Test")]
    [InlineData("Antw: Test", "Test")]
    [InlineData("Reise: Zürich", "Reise: Zürich")]
    [InlineData(null, "")]
    public void Prefixes_are_stripped(string? subject, string expected) =>
        Assert.Equal(expected, MessageComposer.StripPrefixes(subject));

    [Fact]
    public void Html_is_converted_to_readable_text()
    {
        const string html = "<html><head><style>p{color:red}</style></head><body><p>Hallo&nbsp;Anna</p><p>Zeile  mit   Leerzeichen<br>Neue Zeile</p><script>alert(1)</script><ul><li>A</li><li>B</li></ul></body></html>";

        var text = HtmlText.ToPlainText(html);

        Assert.DoesNotContain("color", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
        Assert.Equal("Hallo Anna\n\nZeile mit Leerzeichen\nNeue Zeile\n\nA\nB", text);
    }
}

public class MailActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Delete_moves_to_trash_then_deletes_permanently_from_trash()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Folders["Trash"] = [];
        var message = env.MailServer.Deliver("INBOX", "weg damit", Now);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);
        var folders = await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var trash = folders.Single(f => f.Role == FolderRole.Trash);

        await env.Mail.DeleteAsync(connection, inbox, [message.RemoteId], TestContext.Current.CancellationToken);

        Assert.Empty(env.MailServer.Folders["INBOX"]);
        Assert.Empty(await env.Mail.GetMessagesAsync(inbox, cancellationToken: TestContext.Current.CancellationToken));
        var inTrash = Assert.Single(await env.Mail.GetMessagesAsync(trash, cancellationToken: TestContext.Current.CancellationToken));

        await env.Mail.DeleteAsync(connection, trash, [inTrash.RemoteId], TestContext.Current.CancellationToken);

        Assert.Empty(env.MailServer.Folders["Trash"]);
        Assert.Empty(await env.Mail.GetMessagesAsync(trash, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Archive_requires_archive_folder()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var message = env.MailServer.Deliver("INBOX", "ablegen", Now);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);
        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, TestContext.Current.CancellationToken))[0];

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Mail.ArchiveAsync(connection, inbox, [message.RemoteId], TestContext.Current.CancellationToken));

        env.MailServer.Folders["Archive"] = [];
        await env.Mail.SyncAllAsync(TestContext.Current.CancellationToken);
        await env.Mail.ArchiveAsync(connection, inbox, [message.RemoteId], TestContext.Current.CancellationToken);
        Assert.Single(env.MailServer.Folders["Archive"]);
    }
}
