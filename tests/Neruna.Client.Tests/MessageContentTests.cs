using MimeKit;
using MimeKit.Utils;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class MessageContentTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void Inline_disposition_pdf_is_still_an_attachment()
    {
        // Common in the wild: the PDF has Content-Disposition: inline.
        var pdf = new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream([1, 2, 3])), ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = "Offerte.pdf" } };
        var message = Message(new Multipart("mixed") { new TextPart("plain") { Text = "Anbei die Offerte." }, pdf });

        var content = MessageContent.From(message);

        Assert.Equal("Offerte.pdf", MessageContent.FileNameOf(Assert.Single(content.Attachments)));
        Assert.Contains("Anbei die Offerte.", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_images_are_inline_and_unreferenced_ones_are_attachments()
    {
        var builder = new BodyBuilder();
        var logo = builder.LinkedResources.Add("logo.png", Png, new ContentType("image", "png"));
        logo.ContentId = MimeUtils.GenerateMessageId();
        var unused = builder.LinkedResources.Add("foto.png", Png, new ContentType("image", "png"));
        unused.ContentId = MimeUtils.GenerateMessageId();
        builder.HtmlBody = $"<p>Hallo</p><img src=\"cid:{logo.ContentId}\"><div style=\"background:url(cid:{logo.ContentId})\"></div>";
        builder.TextBody = "Hallo";
        var message = Message(builder.ToMessageBody());

        var content = MessageContent.From(message);

        Assert.Equal(logo.ContentId, Assert.Single(content.InlineParts).Key);
        Assert.Equal("foto.png", MessageContent.FileNameOf(Assert.Single(content.Attachments)));
        Assert.Contains($"cid:{logo.ContentId}", content.Html, StringComparison.Ordinal);
        Assert.Equal("Hallo", content.PlainText);
    }

    [Fact]
    public void Attributes_of_broken_html_do_not_stop_the_message()
    {
        // Unquoted style with a space: the tokenizer sees an attribute named "ui',sans-serif;".
        var message = Message(new TextPart("html") { Text = "<p style=font-family:'Segoe ui',sans-serif; class=x>Hallo <b>Welt</b></p>" });

        var content = MessageContent.From(message);

        Assert.Contains("Hallo <b>Welt</b>", content.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("sans-serif;=", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_handlers_forms_and_remote_content_are_removed()
    {
        const string html = """
            <html><head><script>alert(1)</script><link rel="stylesheet" href="https://evil.example/x.css"><meta http-equiv="refresh" content="0;url=https://evil.example"></head>
            <body onload="steal()"><p onclick="x()">Text</p><a href="javascript:alert(1)">bad</a><a href="https://example.com">gut</a>
            <img src="https://tracker.example/pixel.gif"><div style="background-image: url('https://tracker.example/bg.png'); color: red">Box</div>
            <form action="https://evil.example"><input name="pw"></form><iframe src="https://evil.example"></iframe></body></html>
            """;
        var message = Message(new TextPart("html") { Text = html });

        var content = MessageContent.From(message);

        Assert.True(content.HasBlockedRemoteContent);
        foreach (var forbidden in new[] { "<script", "alert(", "onload", "onclick", "javascript:", "tracker.example", "<form", "<input", "<iframe", "<link", "http-equiv" })
        {
            Assert.DoesNotContain(forbidden, content.Html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("href=\"https://example.com\"", content.Html, StringComparison.Ordinal);
        Assert.Contains("color: red", content.Html, StringComparison.Ordinal);
        Assert.Contains("src=\"data:image/gif;base64,", content.Html, StringComparison.Ordinal);
        Assert.Contains(">Text<", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_images_are_kept_when_allowed()
    {
        var message = Message(new TextPart("html") { Text = "<img src=\"https://example.com/logo.png\">" });

        var content = MessageContent.From(message, allowRemoteContent: true);

        Assert.False(content.HasBlockedRemoteContent);
        Assert.Contains("https://example.com/logo.png", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Calendar_invitation_alternative_and_attached_message_are_attachments()
    {
        var invite = new TextPart("calendar") { Text = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nEND:VCALENDAR\r\n" };
        var inner = new MimeMessage { Subject = "Ursprüngliche Anfrage" };
        inner.Body = new TextPart("plain") { Text = "x" };
        var message = Message(new Multipart("mixed")
        {
            new MultipartAlternative { new TextPart("plain") { Text = "Einladung" }, new TextPart("html") { Text = "<p>Einladung</p>" }, invite },
            new MessagePart { Message = inner },
        });

        var content = MessageContent.From(message);

        Assert.Equal(["einladung.ics", "Ursprüngliche Anfrage.eml"], content.Attachments.Select(MessageContent.FileNameOf));
        Assert.Contains("<p>Einladung</p>", content.Html, StringComparison.Ordinal);
        Assert.Equal("Einladung", content.PlainText);
    }

    [Fact]
    public void Apple_mail_split_text_around_inline_image_keeps_all_text()
    {
        var image = new MimePart("image", "jpeg") { Content = new MimeContent(new MemoryStream(Png)), ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = "IMG_0042.jpeg" } };
        var message = Message(new Multipart("mixed") { new TextPart("plain") { Text = "Vor dem Bild" }, image, new TextPart("plain") { Text = "Nach dem Bild" } });

        var content = MessageContent.From(message);

        Assert.Contains("Vor dem Bild", content.Html, StringComparison.Ordinal);
        Assert.Contains("Nach dem Bild", content.Html, StringComparison.Ordinal);
        Assert.Equal("IMG_0042.jpeg", MessageContent.FileNameOf(Assert.Single(content.Attachments)));
    }

    [Fact]
    public void Thunderbird_flowed_text_has_no_paragraph_per_line()
    {
        var part = new TextPart("plain") { Text = "Hallo Anna\r\n\r\nZeile eins, die lang ist und \r\numgebrochen wurde.\r\nZeile zwei\r\n" };
        part.ContentType.Format = "flowed";
        var message = Message(part);

        var content = MessageContent.From(message);

        Assert.DoesNotContain("<p>", content.Html, StringComparison.Ordinal);
        Assert.Contains("<div>Hallo Anna</div><div><br></div><div>Zeile eins, die lang ist und umgebrochen wurde.</div><div>Zeile zwei</div>", content.Html, StringComparison.Ordinal);
        Assert.Contains("Zeile eins, die lang ist und umgebrochen wurde.", content.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Thunderbird_plain_text_signature_in_html_keeps_its_lines()
    {
        var html = "<html><body><p>Hallo Anna</p><pre class=\"moz-signature\" cols=\"72\">-- \r\nAnna Muster\r\nExample AG    Tel. 044 123 45 67\r\n</pre></body></html>";

        var content = MessageContent.From(Message(new TextPart("html") { Text = html }));

        Assert.DoesNotContain("<pre", content.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("class=\"moz-signature\"", content.Html, StringComparison.Ordinal);
        Assert.Contains("-- <br>Anna Muster<br>Example AG<span style=\"white-space: pre\">    </span>Tel. 044 123 45 67</div>", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Flowed_signature_separator_stays_on_its_own_line()
    {
        var part = new TextPart("plain") { Text = "Gruss \r\nAnna\r\n-- \r\nAnna Muster\r\n" };
        part.ContentType.Format = "flowed";

        var content = MessageContent.From(Message(part));

        Assert.Contains("<div>Gruss Anna</div><div>--</div><div>Anna Muster</div>", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_is_escaped_and_links_become_clickable()
    {
        var message = Message(new TextPart("plain") { Text = "<b>kein html</b>\nSiehe https://example.com" });

        var content = MessageContent.From(message);

        Assert.Contains("&lt;b&gt;kein html&lt;/b&gt;", content.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com\"", content.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Plain_text_lines_are_blocks_without_relying_on_pre_wrap(string newline)
    {
        var message = Message(new TextPart("plain") { Text = string.Join(newline, "Hallo Anna", "", "Name      Menge", "\tEingerückt", "Siehe https://example.com/a?b=1.", "Gruss") });

        var html = MessageContent.From(message).Html;

        Assert.DoesNotContain("pre-wrap", html, StringComparison.Ordinal);
        Assert.Contains("<div>Hallo Anna</div><div><br></div><div>Name<span style=\"white-space: pre\">      </span>Menge</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div><span style=\"white-space: pre\">    </span>Eingerückt</div>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/a?b=1\">https://example.com/a?b=1</a>.", html, StringComparison.Ordinal);
        Assert.Contains("<div>Gruss</div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_html_becomes_text_with_single_line_breaks()
    {
        const string html = "<div style=\"font-family: Calibri\"><div>Hallo Lea</div><div><br></div><div>Zeile zwei</div><div>Zeile drei<br></div><div>Gruss</div></div>";

        Assert.Equal("Hallo Lea\n\nZeile zwei\nZeile drei\nGruss", HtmlText.ToPlainText(html));
    }

    private static MimeMessage Message(MimeEntity body)
    {
        var message = new MimeMessage { Subject = "Test", Body = body };
        message.From.Add(new MailboxAddress("Marco", "marco@example.com"));
        return message;
    }
}

public class MailPreviewTests
{
    [Theory]
    [InlineData("Hallo Anna [cid:c09a909-45kk] Anbei die Offerte<https://example.com/offerte> wie besprochen",
                "Hallo Anna Anbei die Offerte wie besprochen")]
    [InlineData("Siehe <b>hier</b>&nbsp;&amp; dort\r\n\r\nGruss", "Siehe hier & dort Gruss")]
    [InlineData("[image: Logo] Newsletter <mailto:info@example.com>", "Newsletter")]
    [InlineData("Mehr dazu unter<https://example.com/very/long/li", "Mehr dazu unter")]
    [InlineData("3 < 5 und a<b", "3 < 5 und a<b")]
    [InlineData(null, "")]
    public void Technical_leftovers_are_removed(string? preview, string expected) =>
        Assert.Equal(expected, Neruna.Core.Mail.MailPreview.Clean(preview));
}
