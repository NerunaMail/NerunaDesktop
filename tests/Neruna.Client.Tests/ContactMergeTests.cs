using Neruna.Core.Contacts;

namespace Neruna.Client.Tests;

public class ContactMergeTests
{
    private static readonly AddressBookInfo Personal = new(Guid.NewGuid(), "/personal/", "Persönlich", false);
    private static readonly AddressBookInfo Domain = new(Guid.NewGuid(), "/gal/", "Firma", true);

    private static ContactEntry Entry(string remoteId, string vcard, AddressBookInfo? book = null) =>
        new(book ?? Personal, remoteId, VCardReader.Read(vcard));

    private static string Card(string uid, string body) => $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\n{body}END:VCARD\r\n";

    [Fact]
    public void Same_name_shared_address_or_number_are_found_as_one_person()
    {
        var phoneOnly = Entry("1", Card("a", "FN:Anna Muster\r\nN:Muster;Anna;;;\r\nTEL:079 123 45 67\r\n"));
        var mailOnly = Entry("2", Card("b", "FN:Muster Anna\r\nN:Anna;Muster;;;\r\nEMAIL:anna@example.com\r\n"));
        var sameNumber = Entry("3", Card("c", "FN:A. Muster\r\nTEL:+41 79 123 45 67\r\n"));
        var someoneElse = Entry("4", Card("d", "FN:Beat Keller\r\nEMAIL:beat@example.com\r\n"));
        var readOnly = Entry("5", Card("e", "FN:Anna Muster\r\nEMAIL:anna@example.com\r\n"), Domain);
        var group = Entry("6", Card("f", "FN:Anna Muster\r\nX-ADDRESSBOOKSERVER-KIND:group\r\n"));

        var found = Assert.Single(ContactMerge.FindDuplicates([phoneOnly, mailOnly, sameNumber, someoneElse, readOnly, group]));

        Assert.Equal(["1", "2", "3"], found.Select(e => e.RemoteId).Order());
    }

    [Fact]
    public void Merged_card_keeps_everything_of_both()
    {
        var keep = Card("keep", "FN:Anna Muster\r\nN:Muster;Anna;;;\r\nTEL;TYPE=cell:079 123 45 67\r\nNOTE:Kundin seit 2019\r\n"
                                + "item1.ADR:;;Bahnhofstrasse 1;Zürich;;8001;Schweiz\r\nitem1.X-ABLabel:Büro\r\n");
        var other = Card("other", "FN:Anna Muster\r\nN:Muster;Anna;;;\r\nORG:Muster AG\r\nEMAIL;TYPE=INTERNET:anna@example.com\r\n"
                                  + "TEL:+41 79 123 45 67\r\nNOTE:Lieber per Mail\r\nitem1.URL:https://muster.example\r\nitem1.X-ABLabel:Firma\r\n"
                                  + "BDAY:1985-04-12\r\nPHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQ\r\n");

        var merged = ContactMerge.Merge(keep, other);
        var card = VCardReader.Read(merged);

        Assert.Equal("keep", card.Uid);
        Assert.Equal("Muster AG", card.Organization);
        Assert.Equal(["anna@example.com"], card.EmailAddresses);
        Assert.Equal(["079 123 45 67"], card.PhoneNumbers); // the same number, written differently: once
        Assert.Equal("Kundin seit 2019\n\nLieber per Mail", card.Note);
        Assert.NotNull(card.Birthday);
        Assert.NotNull(card.Photo);
        Assert.Contains("item1.ADR:;;Bahnhofstrasse 1", merged, StringComparison.Ordinal);
        Assert.Contains("\r\nURL:https://muster.example\r\n", merged, StringComparison.Ordinal); // without the clashing "item1."
        Assert.Single(merged.Split("BEGIN:VCARD"), s => s.Length > 0);
    }

    [Fact]
    public void Merging_twice_adds_nothing_more()
    {
        var keep = Card("keep", "FN:Anna Muster\r\nEMAIL:anna@example.com\r\nURL:https://muster.example\r\nNOTE:Hallo\r\n");
        var once = ContactMerge.Merge(keep, keep.Replace("UID:keep", "UID:other", StringComparison.Ordinal));

        Assert.Equal(VCardReader.Read(keep).EmailAddresses, VCardReader.Read(once).EmailAddresses);
        Assert.Equal("Hallo", VCardReader.Read(once).Note);
        Assert.Single(once.Split("URL:"), s => s.StartsWith("https", StringComparison.Ordinal));
    }
}
