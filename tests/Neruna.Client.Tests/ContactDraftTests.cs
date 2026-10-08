using Neruna.Core.Contacts;

namespace Neruna.Client.Tests;

public class ContactDraftTests
{
    [Fact]
    public void Editing_keeps_unmanaged_properties_and_uid()
    {
        const string original = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:42\r\nFN:Anna Muster\r\nN:Muster;Anna;;;\r\n"
                                + "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQ\r\nADR;TYPE=HOME:;;Gasse 3;Bern;;3011;Schweiz\r\n"
                                + "X-SOGO-CUSTOM:wert\r\nEMAIL;TYPE=INTERNET:alt@example.com\r\nEND:VCARD\r\n";

        var draft = ContactDraft.FromVCard(original) with { Emails = [new ContactField("neu@example.com", "work")] };
        var updated = draft.ToVCard(original);

        Assert.Contains("UID:42", updated, StringComparison.Ordinal);
        Assert.Contains("PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQ", updated, StringComparison.Ordinal);
        Assert.Contains("ADR;TYPE=HOME:;;Gasse 3;Bern;;3011;Schweiz", updated, StringComparison.Ordinal);
        Assert.Contains("X-SOGO-CUSTOM:wert", updated, StringComparison.Ordinal);
        Assert.Contains("EMAIL;TYPE=INTERNET,WORK:neu@example.com", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("alt@example.com", updated, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(updated, "BEGIN:VCARD"));
    }

    [Fact]
    public void Values_are_escaped_and_read_back()
    {
        var draft = new ContactDraft("Anna", "Muster; Söhne", "Example AG, Zürich", "Leiterin\nIT", [new ContactField("anna@example.com")], [new ContactField("+41 44 000 00 00", "cell")], "Notiz mit \\ Backslash");

        var vcard = draft.ToVCard(null);
        var card = VCardReader.Read(vcard);

        Assert.Equal("Muster; Söhne", card.FamilyName);
        Assert.Equal("Example AG, Zürich", card.Organization);
        Assert.Equal("Leiterin\nIT", card.Title);
        Assert.Equal("Notiz mit \\ Backslash", card.Note);
        Assert.Equal("cell", card.Phones[0].Kind);
        Assert.Equal("Anna Muster; Söhne", card.DisplayName);
    }

    [Fact]
    public void Long_lines_are_folded_without_breaking_utf8()
    {
        var draft = ContactDraft.Empty with { GivenName = "Anna", Note = string.Concat(Enumerable.Repeat("Grüezi mitenand ", 20)) };

        var vcard = draft.ToVCard(null);

        Assert.All(vcard.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Equal(draft.Note!.Trim(), VCardReader.Read(vcard).Note);
    }

    [Fact]
    public void Version_4_cards_stay_version_4()
    {
        const string v4 = "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:1\r\nFN:X\r\nTEL;VALUE=uri;TYPE=cell:tel:+41-79-000-00-00\r\nEND:VCARD\r\n";

        var draft = ContactDraft.FromVCard(v4);
        var updated = draft.ToVCard(v4);

        Assert.Contains("VERSION:4.0", updated, StringComparison.Ordinal);
        Assert.Contains("TEL;VALUE=text;TYPE=CELL:+41-79-000-00-00", updated, StringComparison.Ordinal);
        Assert.Contains("UID:urn:uuid:1", updated, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
