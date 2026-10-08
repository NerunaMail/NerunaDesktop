using Neruna.Core.Contacts;

namespace Neruna.Client.Tests;

public class VCardReaderTests
{
    [Fact]
    public void Reads_display_fields_with_folding_groups_and_escapes()
    {
        const string vcard = "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:42\r\nFN:Anna Muster\r\nN:Muster;Anna;;;\r\n"
                             + "ORG:Example AG\\, Zürich;Verkauf\r\nitem1.EMAIL;TYPE=work:anna.muster@exam\r\n ple.com\r\n"
                             + "EMAIL;TYPE=\"home,pref\":anna@privat.example\r\nTEL;VALUE=uri:tel:+41-44-000-00-00\r\nEND:VCARD\r\n";

        var card = VCardReader.Read(vcard);

        Assert.Equal("urn:uuid:42", card.Uid);
        Assert.Equal("Anna Muster", card.DisplayName);
        Assert.Equal("Example AG, Zürich", card.Organization);
        Assert.Equal(["anna.muster@example.com", "anna@privat.example"], card.EmailAddresses);
        Assert.Equal(["+41-44-000-00-00"], card.PhoneNumbers);
    }

    [Theory]
    [InlineData("N:Muster;Anna;;;", "Anna Muster")]
    [InlineData("ORG:Example AG", "Example AG")]
    [InlineData("EMAIL:x@example.com", "x@example.com")]
    [InlineData("NOTE:nichts", "(ohne Namen)")]
    public void Falls_back_when_FN_is_missing(string line, string expected)
    {
        var card = VCardReader.Read($"BEGIN:VCARD\nVERSION:3.0\n{line}\nEND:VCARD\n");

        Assert.Equal(expected, card.DisplayName);
    }
}
