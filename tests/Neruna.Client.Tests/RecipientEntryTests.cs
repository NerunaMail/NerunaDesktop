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
