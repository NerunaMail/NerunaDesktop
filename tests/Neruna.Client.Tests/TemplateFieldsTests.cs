using Neruna.Core.Contacts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class TemplateFieldsTests
{
    private static ContactCard? None(string email) => null;

    [Fact]
    public void Fields_are_filled_encoded_unknown_ones_stay_and_missing_ones_are_empty()
    {
        var recipient = new TemplateRecipient("anna@example.com", "Anna Muster", "Anna", "Muster", "Muster & Co");
        const string html = "<p>Sehr geehrte Frau {{recipient_last_name}}</p><p>{{ Recipient_Company }} – {{recipient_email}} {{recipient_title}}</p>";

        Assert.Equal("<p>Sehr geehrte Frau Muster</p><p>Muster &amp; Co – anna@example.com {{recipient_title}}</p>", TemplateFields.Fill(html, recipient));
        Assert.Equal("<p>Sehr geehrte Frau </p><p> –  {{recipient_title}}</p>", TemplateFields.Fill(html, null));
        Assert.True(TemplateFields.HasFields(html));
        Assert.False(TemplateFields.HasFields("<p>{{start}} bis {{end}}</p>"));
    }

    [Fact]
    public async Task The_first_recipient_comes_from_the_address_book()
    {
        var card = VCardReader.Read("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Dr. Anna Muster\r\nN:Muster;Anna;;Dr.;\r\nORG:Muster AG\r\nEMAIL:anna@example.com\r\nEND:VCARD\r\n");

        var recipient = await TemplateFields.ResolveAsync("\"A. Muster\" <Anna@Example.com>, marco@example.com",
            email => Task.FromResult<ContactCard?>(email.Equals("anna@example.com", StringComparison.OrdinalIgnoreCase) ? card : null));

        Assert.Equal(new TemplateRecipient("Anna@Example.com", "Dr. Anna Muster", "Anna", "Muster", "Muster AG"), recipient);
    }

    [Theory]
    [InlineData("Anna Muster <anna@example.com>", "Anna Muster", "Anna", "Muster")]
    [InlineData("\"Muster, Anna\" <anna@example.com>", "Muster, Anna", "Anna", "Muster")]
    [InlineData("Anna Maria Muster <anna@example.com>", "Anna Maria Muster", "Anna Maria", "Muster")]
    [InlineData("anna@example.com", null, null, null)]
    [InlineData("Support <support@example.com>", "Support", null, null)]
    public async Task Without_a_contact_the_name_in_the_field_is_split(string to, string? full, string? first, string? last)
    {
        var recipient = await TemplateFields.ResolveAsync(to, email => Task.FromResult(None(email)));

        Assert.Equal((full, first, last), (recipient!.FullName, recipient.FirstName, recipient.LastName));
        Assert.Equal("example.com", recipient.Email.Split('@')[1]);
    }

    [Fact]
    public async Task No_recipient_yet_gives_none()
    {
        Assert.Null(await TemplateFields.ResolveAsync("", email => Task.FromResult(None(email))));
        Assert.Null(await TemplateFields.ResolveAsync("noch kein", email => Task.FromResult(None(email))));
    }
}
