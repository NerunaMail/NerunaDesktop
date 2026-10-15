using System.Text;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Providers.Pst;

namespace Neruna.Client.Tests;

/// <summary>
/// Outlook data files as a read-only account: RTF bodies, the MIME put together from properties, and – with a real
/// file – the provider:
///   NERUNA_TEST_PST=/path/to/example.ost dotnet test
/// (any .pst/.ost with an inbox that has mail).
/// </summary>
public class PstTests
{
    private static readonly string? File = Environment.GetEnvironmentVariable("NERUNA_TEST_PST");

    [Fact]
    public void Html_that_Outlook_wrapped_in_RTF_comes_out_as_it_was()
    {
        // The example of [MS-OXRTFEX] 4.1, shortened: tags in \*\htmltag, RTF-only parts between \htmlrtf and \htmlrtf0.
        const string Rtf = """
            {\rtf1\ansi\ansicpg1252\fromhtml1 \deff0{\fonttbl{\f0\fswiss Arial;}}
            {\*\htmltag19 <html>}{\*\htmltag34 <head>}{\*\htmltag41 </head>}
            {\*\htmltag50 <body>}\htmlrtf {\htmlrtf0
            {\*\htmltag64 <p>}\htmlrtf {\htmlrtf0 Gr\'fcsse aus Z\'fcrich UNICODE8211?\htmlrtf\par }\htmlrtf0
            {\*\htmltag72 </p>}
            {\*\htmltag58 <a href="https://example.com/?a=1&amp;b=2">}Link{\*\htmltag58 </a>}
            \htmlrtf }\htmlrtf0 {\*\htmltag58 </body>}{\*\htmltag27 </html>}}
            """;

        // "\u8211?" written out here: C# would turn \u8211 into a character even in a raw string.
        var html = RtfBody.HtmlOf(Rtf.Replace("UNICODE", "\\u", StringComparison.Ordinal));

        Assert.NotNull(html);
        Assert.Contains("<p>Grüsse aus Zürich –</p>", html.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/?a=1&amp;b=2\">Link</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Arial", html, StringComparison.Ordinal);
        Assert.Null(RtfBody.HtmlOf(@"{\rtf1\ansi Nur Text}"));
    }

    [Fact]
    public void Plain_RTF_becomes_text_without_its_tables()
    {
        const string Rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Calibri;}}{\colortbl;\red0\green0\blue0;}{\*\generator Riched20;}\f0\fs22 Sehr geehrte Frau M\'fcller,\par\par Termin: 14.\~Oktober\tab 10:00\par {\b Danke}\par}";

        var text = RtfBody.TextOf(Rtf);

        Assert.Equal("Sehr geehrte Frau Müller,\r\n\r\nTermin: 14. Oktober\t10:00\r\nDanke", text);
    }

    [Fact]
    public void A_message_is_put_together_from_the_original_headers_its_body_and_its_attachments()
    {
        var picture = new PstAttachment("logo.png", [137, 80, 78, 71], ContentId: "logo@firma", IsInline: true);
        var invoice = new PstAttachment("Rechnung.pdf", Encoding.ASCII.GetBytes("%PDF-1.4"));
        var forwarded = new PstItem(null, "Weitergeleitet", new PstAddress("Lea", "lea@example.com"), [new PstAddress("Anna", "anna@example.com")], [], [],
            new DateTimeOffset(2019, 3, 1, 8, 0, 0, TimeSpan.Zero), "inner@example.com", null, null, "Innen", null, []);
        var item = new PstItem(
            "Received: from mx.example.com by mail.example.com\r\nFrom: \"Marco Bernasconi\" <marco@bernasconi.example>\r\nTo: anna@example.com\r\nSubject: =?utf-8?q?Offerte_f=C3=BCr_2019?=\r\nDate: Fri, 01 Mar 2019 09:00:00 +0100\r\nMessage-ID: <offerte@bernasconi.example>\r\nContent-Type: text/plain\r\nMIME-Version: 1.0\r\n",
            "Offerte für 2019",
            new PstAddress("Bernasconi", "/O=EXCHANGE/OU=FIRST/CN=MARCO"),
            [new PstAddress("Anna", "anna@example.com")],
            [],
            [],
            null,
            "offerte@bernasconi.example",
            null,
            "<p>Siehe Anhang</p><img src=\"cid:logo@firma\">",
            "Siehe Anhang",
            null,
            [picture, invoice, new PstAttachment("Weitergeleitet.eml", [], Message: forwarded)]);

        var ct = TestContext.Current.CancellationToken;
        var message = PstMime.Build(item);
        using var stream = new MemoryStream();
        message.WriteTo(stream, ct);
        stream.Position = 0;
        var reread = MimeMessage.Load(stream, ct);

        // The original headers, except the ones describing the old body.
        Assert.Equal("Offerte für 2019", reread.Subject);
        Assert.Equal("marco@bernasconi.example", reread.From.Mailboxes.Single().Address);
        Assert.Equal("offerte@bernasconi.example", reread.MessageId);
        Assert.Contains(reread.Headers, h => h.Field == "Received");
        Assert.Equal("multipart/mixed", reread.Body?.ContentType.MimeType);

        // Text and HTML as alternatives; the picture next to the HTML; the PDF and the mail as attachments.
        Assert.Equal("Siehe Anhang", reread.TextBody?.Trim());
        Assert.Contains("cid:logo@firma", reread.HtmlBody ?? string.Empty, StringComparison.Ordinal);
        var related = reread.BodyParts.OfType<MimePart>().Single(p => p.ContentId == "logo@firma");
        Assert.Equal("image/png", related.ContentType.MimeType);
        Assert.Equal(["Rechnung.pdf"], reread.Attachments.OfType<MimePart>().Select(a => a.FileName));
        var inner = Assert.Single(reread.Attachments.OfType<MessagePart>()).Message;
        Assert.NotNull(inner);
        Assert.Equal(("Weitergeleitet", "Innen"), (inner.Subject, inner.TextBody?.Trim()));

        // Without headers: from the properties; an Exchange-only address keeps just the name.
        var bare = PstMime.Build(item with { Headers = null, Text = null, Html = null, Rtf = @"{\rtf1\ansi Hallo}", Attachments = [], Date = new DateTimeOffset(2019, 3, 1, 8, 0, 0, TimeSpan.Zero) });
        Assert.Equal(("Bernasconi", string.Empty), (bare.From.Mailboxes.Single().Name, bare.From.Mailboxes.Single().Address));
        Assert.Equal("Hallo", bare.TextBody?.Trim());
        Assert.Equal(new DateTimeOffset(2019, 3, 1, 8, 0, 0, TimeSpan.Zero), bare.Date);
    }

    [Fact]
    public async Task A_real_data_file_is_read_as_an_account_that_does_not_change()
    {
        Assert.SkipWhen(File is null, "NERUNA_TEST_PST not set");
        var ct = TestContext.Current.CancellationToken;
        var account = PstMailProviderFactory.AccountFor(File!);
        var connection = account.Connections.Single();
        Assert.Equal((null, ProviderIds.Pst, ServiceKind.Mail), (account.EmailAddress, connection.ProviderId, connection.Kind));
        await using var provider = new PstMailProviderFactory().Create(connection);
        await provider.TestConnectionAsync(ct);

        // The person's folders – not the store's own (views, search, free/busy) nor calendar and contacts.
        var folders = await provider.GetFoldersAsync(ct);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox && f.ParentRemoteId is null);
        Assert.DoesNotContain(folders, f => f.Name is "Calendar" or "Contacts" or "IPM_VIEWS" or "Search Root" or "Freebusy Data");

        var first = await provider.SyncFolderAsync(inbox, [], ct);
        Assert.NotEmpty(first.AddedOrChanged);
        var summary = first.AddedOrChanged[0];
        Assert.False(string.IsNullOrEmpty(summary.Subject));

        var message = await provider.GetMessageAsync(inbox, summary.RemoteId, ct);
        Assert.Equal(summary.Subject, message.Subject);
        Assert.True(message.HtmlBody is not null || message.TextBody is not null);

        // The same file again: nothing to do, and read/flagged set in Neruna stay.
        var again = await provider.SyncFolderAsync(inbox with { SyncState = first.NewSyncState }, [.. first.AddedOrChanged.Select(s => s.RemoteId)], ct);
        Assert.Empty(again.AddedOrChanged);
        Assert.Empty(again.RemovedRemoteIds);
        await provider.SetFlagsAsync(inbox, [summary.RemoteId], MessageFlags.Seen, false, ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(inbox, [summary.RemoteId], ct));
        Assert.False(provider.Capabilities.HasFlag(MailProviderCapabilities.Append));
    }
}
