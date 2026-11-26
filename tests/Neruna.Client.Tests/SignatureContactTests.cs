using Neruna.Core.Contacts;

namespace Neruna.Client.Tests;

public class SignatureContactTests
{
    [Fact]
    public void A_typical_signature_gives_phones_organisation_title_address_and_website()
    {
        const string body = """
            Hallo Patrik

            Die Offerte folgt morgen.

            Freundliche Grüsse

            Hans Muster
            Geschäftsführer
            Muster Treuhand AG
            Bahnhofstrasse 12
            8001 Zürich
            Tel. +41 44 123 45 67
            Mobil 079 765 43 21
            www.muster-treuhand.ch
            """;

        var found = SignatureContact.ReadText(body, "Hans Muster", "hans@muster-treuhand.ch");

        Assert.Equal("Muster Treuhand AG", found.Organization);
        Assert.Equal("Geschäftsführer", found.Title);
        Assert.Equal([new ContactField("+41 44 123 45 67", "work"), new ContactField("079 765 43 21", "cell")], found.Phones);
        var address = Assert.Single(found.Addresses!);
        Assert.Equal(("Bahnhofstrasse 12", "8001", "Zürich"), (address.Street, address.PostalCode, address.City));
        Assert.Equal("https://www.muster-treuhand.ch", Assert.Single(found.Websites!).Value);
    }

    [Fact]
    public void A_one_line_signature_and_quoted_mails_below_it()
    {
        const string body = """
            Passt so, danke.

            --
            Lea Keller | Keller GmbH | Seestrasse 5, 6003 Luzern | T +41 41 210 33 44 | F +41 41 210 33 45

            Am 12.03.2026 um 10:15 schrieb Anna Beispiel <anna@example.com>:
            > Tel. 031 999 88 77
            > Weg 1
            > 3000 Bern
            """;

        var found = SignatureContact.ReadText(body, "Lea Keller", "lea@keller.example");

        Assert.Equal("Keller GmbH", found.Organization);
        Assert.Equal([new ContactField("+41 41 210 33 44", "work"), new ContactField("+41 41 210 33 45", "fax")], found.Phones);
        var address = Assert.Single(found.Addresses!);
        Assert.Equal(("Seestrasse 5", "6003", "Luzern"), (address.Street, address.PostalCode, address.City));
    }

    [Fact]
    public void Bank_details_dates_and_social_links_are_no_contact_data()
    {
        const string body = """
            Beste Grüsse
            Max Beispiel
            IBAN CH93 0076 2011 6238 5295 7
            UID CHE-123.456.789 MWST
            Termin am 12.03.2026
            https://www.linkedin.com/in/max
            """;

        var found = SignatureContact.ReadText(body, "Max Beispiel", "max@example.com");

        Assert.Empty(found.Phones);
        Assert.Null(found.Websites);
        Assert.True(SignatureContact.IsEmpty(found));
    }

    [Fact]
    public void Completing_a_contact_adds_and_never_replaces()
    {
        var existing = ContactDraft.Empty with
        {
            GivenName = "Hans",
            FamilyName = "Muster",
            Organization = "Muster AG",
            Emails = [new ContactField("hans@muster.ch")],
            Phones = [new ContactField("044 123 45 67", "work")],
        };
        var fromMail = ContactDraft.Empty with
        {
            GivenName = "H.",
            FamilyName = "Muster",
            Organization = "Muster Treuhand AG",
            Title = "Geschäftsführer",
            Emails = [new ContactField("HANS@muster.ch"), new ContactField("h.muster@gmail.example")],
            Phones = [new ContactField("+41 44 123 45 67", "work"), new ContactField("079 765 43 21", "cell")],
            Addresses = [new ContactAddress("Bahnhofstrasse 12", "8001", "Zürich", null, "work")],
        };

        var completed = existing.CompletedWith(fromMail);

        Assert.Equal(("Hans", "Muster", "Muster AG", "Geschäftsführer"), (completed.GivenName, completed.FamilyName, completed.Organization, completed.Title));
        Assert.Equal(["hans@muster.ch", "h.muster@gmail.example"], completed.Emails.Select(e => e.Value));
        Assert.Equal(["044 123 45 67", "079 765 43 21"], completed.Phones.Select(p => p.Value));
        Assert.Single(completed.Addresses!);
    }

    [Fact]
    public void Addresses_and_websites_are_read_and_written_in_the_vcard()
    {
        const string original = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:7\r\nFN:Anna Muster\r\nN:Muster;Anna;;;\r\n"
                                + "ADR;TYPE=HOME:Postfach 4;;Gasse 3;Bern;BE;3011;Schweiz\r\nLABEL;TYPE=HOME:Gasse 3\\n3011 Bern\r\n"
                                + "URL:https://anna.example\r\nEND:VCARD\r\n";

        var card = VCardReader.Read(original);
        var address = Assert.Single(card.Addresses);
        Assert.Equal(("Gasse 3", "3011", "Bern", "Schweiz", "home", "Postfach 4", "BE"),
            (address.Street, address.PostalCode, address.City, address.Country, address.Kind, address.PoBox, address.Region));
        Assert.Equal("https://anna.example", Assert.Single(card.Websites).Value);

        var draft = ContactDraft.FromVCardWithAddresses(original);
        var updated = (draft with { Addresses = [.. draft.Addresses!, new ContactAddress("Seeweg 1", "6000", "Luzern", null, "work")] }).ToVCard(original);

        Assert.Contains("ADR;TYPE=HOME:Postfach 4;;Gasse 3;Bern;BE;3011;Schweiz", updated, StringComparison.Ordinal);
        Assert.Contains("ADR;TYPE=WORK:;;Seeweg 1;Luzern;;6000;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("LABEL", updated, StringComparison.Ordinal);
        Assert.Single(VCardReader.Read(updated).Websites);

        // An editor that does not show addresses leaves them exactly as they are.
        var untouched = ContactDraft.FromVCard(original).ToVCard(original);
        Assert.Contains("LABEL;TYPE=HOME", untouched, StringComparison.Ordinal);
    }
}
