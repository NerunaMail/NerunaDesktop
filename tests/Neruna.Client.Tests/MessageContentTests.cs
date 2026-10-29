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
    public void Picture_sizes_in_inches_and_points_become_pixels()
    {
        // Outlook signatures: the renderer ignored these units and showed the pictures at full size.
        var message = Message(new TextPart("html")
        {
            Text = "<img width=\"45\" height=\"66\" style=\"width:.4687in;height:.6875in\" src=\"cid:a@x\">"
                   + "<img style=\"width:34pt; max-height:1.2cm\" src=\"cid:a@x\"><p style=\"font-size:11.0pt\">Text</p>",
        });

        var html = MessageContent.From(message, forRenderer: true).Html;

        Assert.Contains("style=\"width:45px;height:66px\"", html, StringComparison.Ordinal);
        Assert.Contains("width:45.3px; max-height:45.4px", html, StringComparison.Ordinal);
        Assert.Contains("font-size:11.0pt", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Divs_laid_out_as_tables_become_blocks_real_tables_stay()
    {
        // Newsletter builder layout: with these the reading pane showed nothing at all.
        var message = Message(new TextPart("html")
        {
            Text = "<div style=\"display: table;width: 100%\"><div style=\"min-width: 600px;display: table-cell;vertical-align: top;\">Dear Sir</div></div>"
                   + "<table style=\"display: table\"><tr><td style=\"display:table-cell\">Zelle</td></tr></table>",
        });

        var html = MessageContent.From(message, forRenderer: true).Html;

        Assert.Contains("<div style=\"display: block;width: 100%\">", html, StringComparison.Ordinal);
        Assert.Contains("min-width: 600px;display: block;vertical-align: top;", html, StringComparison.Ordinal);
        Assert.Contains("<table style=\"display: table\">", html, StringComparison.Ordinal);
        Assert.Contains("<td style=\"display:table-cell\">", html, StringComparison.Ordinal);
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
    public void A_blocked_picture_with_only_a_width_does_not_become_a_screen_high_square()
    {
        // Newsletters give big pictures only a width ("100%"); the 1×1 placeholder would be stretched to a square.
        const string html = """
            <img src="https://example.com/hero.jpg" width="100%">
            <img src="https://example.com/logo.jpg" width="150px" style="margin:10px auto;">
            <img src="https://example.com/banner.jpg" width="600" height="200">
            <img src="https://example.com/box.jpg" style="height: 80px">
            <img src="https
             ://example.com/broken-over-lines.jpg" width="100%">
            <img src="https://example.com/chart.png" width="580" height="auto" style="width:100%;height:auto">
            """;
        var content = MessageContent.From(Message(new TextPart("html") { Text = html }));

        var pictures = System.Text.RegularExpressions.Regex.Matches(content.Html, "<img[^>]*>").Select(m => m.Value).ToList();
        Assert.Equal(6, pictures.Count);
        Assert.Contains("height: 1px", pictures[0], StringComparison.Ordinal);
        Assert.DoesNotContain("width", pictures[0], StringComparison.Ordinal);
        Assert.Contains("height=\"1\"", pictures[0], StringComparison.Ordinal);
        Assert.Contains("width=\"600\"", pictures[2], StringComparison.Ordinal);
        Assert.Contains("margin:10px auto; height: 1px", pictures[1], StringComparison.Ordinal);
        Assert.Contains("height: 200px", pictures[2], StringComparison.Ordinal);
        Assert.DoesNotContain("height: 1px", pictures[3], StringComparison.Ordinal);
        Assert.Contains("80px", pictures[3], StringComparison.Ordinal);
        Assert.Contains("height: 1px", pictures[4], StringComparison.Ordinal);
        Assert.DoesNotContain("width=", pictures[5], StringComparison.Ordinal);
        Assert.DoesNotContain("height=\"auto\"", pictures[5], StringComparison.Ordinal);
        Assert.Contains("height=\"1\"", pictures[5], StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", content.Html, StringComparison.Ordinal);
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
    public void Apple_mail_html_in_mixed_alternative_keeps_attachment_and_picture()
    {
        // Apple Mail with an attachment and a picture in the signature: the HTML alternative is multipart/mixed, the
        // text split around them, the picture without Content-ID.
        var zip = new MimePart("application", "zip") { Content = new MimeContent(new MemoryStream([1, 2, 3])), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "v2026.zip" } };
        var logo = new MimePart("image", "png") { Content = new MimeContent(new MemoryStream(Png)), ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = "Logo.png" } };
        var message = Message(new MultipartAlternative
        {
            new TextPart("plain") { Text = "Hallo Anna\n\nFreundliche Grüsse" },
            new Multipart("mixed")
            {
                new TextPart("html") { Text = "<html><body><div>Hallo Anna</div></body></html>" },
                zip,
                new TextPart("html") { Text = "<html><body><div>Freundliche Grüsse</div></body></html>" },
                logo,
                new TextPart("html") { Text = "<html><body><div>Muster AG</div></body></html>" },
            },
        });

        var content = MessageContent.From(message);

        Assert.Contains("<div>Hallo Anna</div>", content.Html, StringComparison.Ordinal);
        Assert.Contains("Muster AG", content.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<hr", content.Html, StringComparison.Ordinal);
        Assert.Equal("v2026.zip", MessageContent.FileNameOf(Assert.Single(content.Attachments)));
        var (id, picture) = Assert.Single(content.InlineParts);
        Assert.Same(logo, picture);
        Assert.Contains("cid:" + id, content.Html, StringComparison.Ordinal);
        Assert.True(content.Html.IndexOf("cid:" + id, StringComparison.Ordinal) < content.Html.IndexOf("Muster AG", StringComparison.Ordinal));
        Assert.Contains("Freundliche Grüsse", content.PlainText, StringComparison.Ordinal);
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

        var content = MessageContent.From(Message(new TextPart("html") { Text = html }), forRenderer: true);

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

    private static string HtmlOf(string html) =>
        MessageContent.From(new MimeMessage { Body = new TextPart("html") { Text = html } }).Html;

    // As for the PDF export's renderer, which needs the tables rewritten.
    private static string RendererHtmlOf(string html) =>
        MessageContent.From(new MimeMessage { Body = new TextPart("html") { Text = html } }, forRenderer: true).Html;

    [Fact]
    public void Colspan_beyond_the_columns_of_the_table_is_cut_down()
    {
        // Newsletter: colspan="3" in a one-column table – the renderer showed nothing at all.
        var html = RendererHtmlOf("<table><tr><td colspan=\"3\">a</td></tr><tr><td>b</td></tr></table>"
            + "<table><tr><td>x</td><td>y</td><td>z</td></tr><tr><td colspan=\"3\">w</td></tr></table>");

        Assert.Contains("colspan=\"1\">a", html);
        Assert.Contains("colspan=\"3\">w", html);
    }

    [Fact]
    public void Web_views_get_the_layout_as_the_sender_wrote_it()
    {
        var html = HtmlOf("<table align=\"center\" style=\"max-width: 500px\"><tr><td>a</td></tr></table>"
            + "<div style=\"display: table-cell; width: 1in\">b</div>");

        Assert.Contains("align=\"center\"", html);
        Assert.Contains("max-width: 500px", html);
        Assert.Contains("display: table-cell; width: 1in", html);
    }

    [Fact]
    public void Tables_lose_max_width_and_centering_becomes_auto_margins()
    {
        var html = RendererHtmlOf("<table align=\"center\" width=\"600\"><tr><td>a</td></tr></table>"
            + "<table style=\"width: 90%; max-width: 500px; margin: auto;\"><tr><td>b</td></tr></table>");

        Assert.DoesNotContain("align=", html);
        Assert.DoesNotContain("max-width", html);
        Assert.Equal(2, html.Split("margin-left: auto; margin-right: auto").Length - 1);
        Assert.Contains("width: 90%", html);
    }

    [Fact]
    public void Cells_get_the_background_of_their_row()
    {
        var html = RendererHtmlOf("<table><tr style=\"background-color: #7FA8DF\"><td style=\"color: white\">Ticket</td><td bgcolor=\"#fff\">own</td></tr>"
            + "<tr><td>plain</td></tr></table>");

        Assert.Contains("<td style=\"background-color: #7FA8DF; color: white\">Ticket", html);
        Assert.Contains("<td bgcolor=\"#fff\">own", html);
        Assert.Contains("<td>plain", html);
    }

    [Fact]
    public void Content_between_table_cells_is_dropped()
    {
        // Outlook: empty bookmark spans between the cells of a row – the renderer took them for cells.
        var html = RendererHtmlOf("<table><tr><td>a</td><span style=\"mso-bookmark:_Hlk1\"><span>x</span></span><td>b</td></tr></table>"
            + "<table><tr><td><table><tr><td>inner</td></tr></table><span>kept</span></td></tr></table>");

        Assert.Contains("<td>a</td><td>b</td>", html);
        Assert.DoesNotContain(">x<", html);
        Assert.Contains("<span>kept</span>", html);
    }

    [Fact]
    public void Links_to_files_are_text_unless_the_sender_is_safe()
    {
        var message = new MimeMessage { Body = new TextPart("html") { Text = "<p>Offerte: <a href=\"file://server/projekte/offerte.pdf\">hier</a> und <a href=\"file:///C:/Daten/plan.xlsx\">C:\\Daten\\plan.xlsx</a></p>" } };

        var text = MessageContent.From(message).Html;
        Assert.DoesNotContain("href=\"file:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hier</span> (\\\\server\\projekte\\offerte.pdf)", text, StringComparison.Ordinal); // the path shown, to copy
        Assert.DoesNotContain("plan.xlsx (", text, StringComparison.Ordinal); // the text shows the path already

        Assert.Contains("href=\"file://server/projekte/offerte.pdf\"", MessageContent.From(message, fileLinks: true).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void File_links_round_trip_and_programs_are_recognised()
    {
        Assert.Equal(@"\\server\projekte\offerte.pdf", FileLinks.ToPath(FileLinks.ToUri(@"\\server\projekte\offerte.pdf")));
        Assert.Equal("/home/anna/Offerte 1.pdf", FileLinks.ToPath(FileLinks.ToUri("/home/anna/Offerte 1.pdf")));
        Assert.Null(FileLinks.ToPath("https://example.com/a.pdf"));
        Assert.True(FileLinks.IsExecutable(@"C:\Tools\setup.EXE"));
        Assert.False(FileLinks.IsExecutable("offerte.pdf"));
    }
}
