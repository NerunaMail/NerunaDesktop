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
    public void Signed_and_encrypted_mail_comes_as_SMIME_for_Neruna_to_open()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Anna Muster", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var envelope = new System.Security.Cryptography.Pkcs.EnvelopedCms(new System.Security.Cryptography.Pkcs.ContentInfo(Encoding.ASCII.GetBytes("Content-Type: text/plain\r\n\r\nGeheim")));
        envelope.Encrypt(new System.Security.Cryptography.Pkcs.CmsRecipient(certificate));
        var item = new PstItem("Subject: Vertraulich\r\nFrom: lea@example.com\r\n", "Vertraulich", null, [], [], [], null, null, null, null, null, null, []);

        var encrypted = PstMime.Build(item with { Smime = envelope.Encode() });
        Assert.Equal(MimeKit.Cryptography.SecureMimeType.EnvelopedData, Assert.IsType<MimeKit.Cryptography.ApplicationPkcs7Mime>(encrypted.Body).SecureMimeType);
        Assert.Equal("Vertraulich", encrypted.Subject);

        const string Signed = "Content-Type: multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=\"b\"\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nUnterschrieben\r\n--b\r\nContent-Type: application/pkcs7-signature; name=smime.p7s\r\nContent-Transfer-Encoding: base64\r\n\r\nAAAA\r\n--b--\r\n";
        var signed = PstMime.Build(item with { Smime = Encoding.ASCII.GetBytes(Signed), SmimeIsMime = true });
        Assert.Equal("multipart/signed", signed.Body?.ContentType.MimeType);
        Assert.Equal("Unterschrieben", signed.TextBody?.Trim());

        // Not PKCS#7 at all: the normal body (here empty) instead of a broken one.
        Assert.Equal("text/plain", PstMime.Build(item with { Smime = [1, 2, 3] }).Body?.ContentType.MimeType);
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

        // The folder object is kept: its contents table is read once per folder, not once per message.
        var archive = PstArchive.For(File!);
        Assert.True(await archive.ReadAsync(a => ReferenceEquals(a.Folder(inbox.RemoteId), a.Folder(inbox.RemoteId)), ct));
        Assert.True(message.HtmlBody is not null || message.TextBody is not null);

        // First a quick pass (the contents table), then one for Outlook's flags, then nothing as long as the file stays.
        Assert.EndsWith("|quick", first.NewSyncState, StringComparison.Ordinal);
        var flags = await provider.SyncFolderAsync(inbox with { SyncState = first.NewSyncState }, [.. first.AddedOrChanged.Select(s => s.RemoteId)], ct);
        Assert.Empty(flags.AddedOrChanged);
        Assert.All(flags.FlagUpdates.Keys, id => Assert.Contains(first.AddedOrChanged, s => s.RemoteId == id));
        Assert.DoesNotContain("|", flags.NewSyncState, StringComparison.Ordinal);

        // The same file again: nothing to do, and read/flagged set in Neruna stay.
        var again = await provider.SyncFolderAsync(inbox with { SyncState = flags.NewSyncState }, [.. first.AddedOrChanged.Select(s => s.RemoteId)], ct);
        Assert.Empty(again.AddedOrChanged);
        Assert.Empty(again.FlagUpdates);
        Assert.Empty(again.RemovedRemoteIds);
        await provider.SetFlagsAsync(inbox, [summary.RemoteId], MessageFlags.Seen, false, ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(inbox, [summary.RemoteId], ct));
        Assert.False(provider.Capabilities.HasFlag(MailProviderCapabilities.Append));
    }

    [Fact]
    public void The_sender_comes_from_its_one_off_entry_id()
    {
        // As in an OST of an IMAP account: provider UID, Unicode flag, name, "SMTP", address.
        var entry = Convert.FromHexString("00000000812B1FA4BEA310199D6E00DD010F54020000018065006E00640065007200200069006E0066006F0072006D0061007400690063007300000053004D005400500000006E006100670069006F007300400065002D0069006E0066006F002E00630068000000");
        Assert.Equal(("ender informatics", "nagios@e-info.ch"), PstArchive.OneOff(entry));

        var exchange = (byte[])entry.Clone();
        exchange[4] = 0xDC; // another provider: an address book entry, no SMTP address in it
        Assert.Null(PstArchive.OneOff(exchange));
        Assert.Null(PstArchive.OneOff([1, 2, 3]));
    }

    [Theory]
    [InlineData("Sent Messages", FolderRole.Sent)]
    [InlineData("Sent", FolderRole.Sent)]
    [InlineData("Gesendete Elemente", FolderRole.Sent)]
    [InlineData("Trash", FolderRole.Trash)]
    [InlineData("Papierkorb", FolderRole.Trash)]
    [InlineData("Junk", FolderRole.Junk)]
    [InlineData("Spam", FolderRole.Junk)]
    [InlineData("Inbox", FolderRole.Inbox)]
    [InlineData("Projekte", FolderRole.None)]
    public void Folders_of_an_imap_account_in_an_ost_get_their_roles(string name, FolderRole role) =>
        Assert.Equal(role, PstArchive.RoleOf(name, null));

    [Fact]
    public void The_top_is_the_folder_with_outlooks_own_folders_not_a_persons_folder_with_more_subfolders()
    {
        // A PST: the top with inbox, sent and "Ferien"; "Ferien" itself has more subfolders than the top.
        string[] top = ["Posteingang", "Gesendete Elemente", "Gelöschte Elemente", "Ferien", "Kunden"];
        string[] ferien = ["2019", "2020", "2021", "2022", "2023", "2024", "2025"];
        Assert.Equal(1, PstArchive.BestTop([[], top, ferien, ["2019 Fotos"]]));

        // Without Outlook's own folders the one with the most mail folders still wins; with none at all, none.
        Assert.Equal(1, PstArchive.BestTop([["Projekte"], ["A", "B", "C"]]));
        Assert.Equal(-1, PstArchive.BestTop([[], []]));
    }

    [Fact]
    public void Outlooks_sync_issues_are_not_mail()
    {
        Assert.True(PstArchive.IsSyncIssues("Sync Issues (This computer only)"));
        Assert.True(PstArchive.IsSyncIssues("Synchronisierungsprobleme (Nur dieser Computer)"));
        Assert.False(PstArchive.IsSyncIssues("Inbox"));
    }
}
