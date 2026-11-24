using Neruna.Core.Contacts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class RecipientEntryTests
{
    private static RecipientEntry Contact(string name, string address, string? organization = null) =>
        new(name, [new MailAddress(name, address)], organization, IsGroup: false);

    [Fact]
    public void Suggestions_put_name_starts_first_then_word_and_address_starts_then_anything_else()
    {
        RecipientEntry[] all =
        [
            Contact("Marco Bernasconi", "marco@bernasconi.example"),
            Contact("Anna Muster", "anna@example.com", "Muster Informatik AG"),
            Contact("Lea Keller", "lea.keller@example.com"),
            Contact("Kellerei Brunner", "info@kellerei.example"),
            Contact("Thomas Frei", "t.frei@mustermann.example"),
        ];

        Assert.Equal(["Kellerei Brunner", "Lea Keller"], RecipientEntry.Find(all, "kel").Select(e => e.Name));
        Assert.Equal(["Anna Muster", "Thomas Frei"], RecipientEntry.Find(all, "muster").Select(e => e.Name));
        Assert.Equal(["Lea Keller"], RecipientEntry.Find(all, "lea.k").Select(e => e.Name));
        Assert.Empty(RecipientEntry.Find(all, "xyz"));
        Assert.Equal(5, RecipientEntry.Find(all, "ex").Count);
        Assert.Empty(RecipientEntry.Find(all, "ex", startsOnly: true));
        Assert.Single(RecipientEntry.Find(all, "a", max: 1));
    }

    [Fact]
    public void Suggestions_put_addresses_written_to_often_and_lately_first()
    {
        RecipientEntry[] all =
        [
            Contact("Anna Muster", "anna@example.com"),
            Contact("Andrea Frei", "andrea@example.com"),
            Contact("Hans Andermatt", "hans@example.com"),
            Contact("Anita Keller", "anita@example.com"),
        ];
        var now = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
        var usage = RecipientUsage.Weigh(
        [
            // Andrea: often, but long ago; Hans: twice last week; Anita: once yesterday.
            .. Enumerable.Range(0, 5).Select(_ => ("ANDREA@example.com", now.AddDays(-300))),
            ("hans@example.com", now.AddDays(-7)),
            ("hans@example.com", now.AddDays(-6)),
            ("anita@example.com", now.AddDays(-1)),
        ], now);

        // A word start ("Andermatt") comes before a name start once written to; never written to: last.
        Assert.Equal(["Hans Andermatt", "Anita Keller", "Andrea Frei", "Anna Muster"],
            RecipientEntry.Find(all, "an", startsOnly: true, usage: usage).Select(e => e.Name));
        Assert.Equal(["Andrea Frei", "Anita Keller", "Anna Muster", "Hans Andermatt"],
            RecipientEntry.Find(all, "an", startsOnly: true).Select(e => e.Name));
    }

    [Fact]
    public async Task Usage_comes_from_the_sent_folders_only()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var store = env.MailStore;
        var connectionId = Guid.NewGuid();
        await env.Accounts.SaveAccountAsync(new Neruna.Core.Accounts.Account(Guid.NewGuid(), "Anna", "anna@example.com",
            [new Neruna.Core.Accounts.ServiceConnection(connectionId, Neruna.Core.Accounts.ServiceKind.Mail, "imap", new Dictionary<string, string>())]), ct);
        var folders = await store.MergeFoldersAsync(connectionId,
            [new MailFolder(connectionId, "INBOX", "INBOX", null, FolderRole.Inbox), new MailFolder(connectionId, "Sent", "Gesendet", null, FolderRole.Sent)], ct);
        MessageSummary Message(string id, string to, DateTimeOffset date) =>
            new(id, null, null, "Betreff", null, [new MailAddress("Lea", to)], date, MessageFlags.None, 1, false, null);
        var now = DateTimeOffset.UtcNow;
        await store.ApplySyncResultAsync(folders.Single(f => f.Role == FolderRole.Sent), new FolderSyncResult("1", true,
            [Message("1", "lea@example.com", now.AddDays(-1)), Message("2", "lea@example.com", now.AddDays(-400))], new Dictionary<string, MessageFlags>(), [], 2, 0), ct);
        await store.ApplySyncResultAsync(folders.Single(f => f.Role == FolderRole.Inbox), new FolderSyncResult("1", true,
            [Message("3", "ich@example.com", now)], new Dictionary<string, MessageFlags>(), [], 1, 0), ct);

        var usage = await env.Mail.GetRecipientUsageAsync(ct);

        Assert.Equal("lea@example.com", Assert.Single(usage).Key);
        Assert.InRange(usage["LEA@example.com"], 0.98, 1.0);
    }

    [Fact]
    public void Field_text_quotes_names_that_need_it()
    {
        Assert.Equal("Anna Muster <anna@example.com>", RecipientEntry.Format(new MailAddress("Anna Muster", "anna@example.com")));
        Assert.Equal("\"Muster, Anna\" <anna@example.com>", RecipientEntry.Format(new MailAddress("Muster, Anna", "anna@example.com")));
        Assert.Equal("\"Dr. Anna \\\"Anni\\\" Muster\" <anna@example.com>", RecipientEntry.Format(new MailAddress("Dr. Anna \"Anni\" Muster", "anna@example.com")));
        Assert.True(MimeKit.MailboxAddress.TryParse(RecipientEntry.Format(new MailAddress("Dr. Anna \"Anni\" Muster", "anna@example.com")), out var parsed));
        Assert.Equal("Dr. Anna \"Anni\" Muster", parsed.Name);
        Assert.Equal("gast@example.org", RecipientEntry.Format(new MailAddress(null, "gast@example.org")));
    }
}
