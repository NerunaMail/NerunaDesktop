using Neruna.Core.Contacts;

namespace Neruna.Client.Tests;

public class ContactGroupTests
{
    [Fact]
    public void Apple_and_sogo_style_group_with_chosen_address_and_external_member()
    {
        const string vcard = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:grp-1\r\nFN:Projektteam\r\nN:Projektteam;;;;\r\nX-ADDRESSBOOKSERVER-KIND:group\r\n"
                             + "X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:lea\r\nX-ADDRESSBOOKSERVER-MEMBER;X-NERUNA-EMAIL=\"marco.privat@example.com\":urn:uuid:marco\r\n"
                             + "X-ADDRESSBOOKSERVER-MEMBER:mailto:extern@example.org\r\nEND:VCARD\r\n";

        var card = VCardReader.Read(vcard);

        Assert.True(card.IsGroup);
        Assert.Equal("Projektteam", card.DisplayName);
        Assert.Equal([new GroupMember("lea", null), new GroupMember("marco", "marco.privat@example.com"), new GroupMember(null, "extern@example.org")], card.Members);
    }

    [Fact]
    public void Vcard4_group_is_read_and_written_back_in_its_own_style()
    {
        const string vcard = "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:grp-2\r\nFN:Vorstand\r\nKIND:group\r\nMEMBER:urn:uuid:anna\r\nCATEGORIES:Verein\r\nEND:VCARD\r\n";
        var draft = GroupDraft.FromVCard(vcard);
        Assert.Equal([new GroupMember("anna", null)], draft.Members);

        var written = (draft with { Members = [.. draft.Members, new GroupMember("beat", "beat@work.example")] }).ToVCard(vcard);

        Assert.Contains("KIND:group", written, StringComparison.Ordinal);
        Assert.Contains("MEMBER:urn:uuid:anna", written, StringComparison.Ordinal);
        Assert.Contains("MEMBER;X-NERUNA-EMAIL=\"beat@work.example\":urn:uuid:beat", written, StringComparison.Ordinal);
        Assert.Contains("UID:urn:uuid:grp-2", written, StringComparison.Ordinal);
        Assert.Contains("CATEGORIES:Verein", written, StringComparison.Ordinal);
        Assert.DoesNotContain("X-ADDRESSBOOKSERVER", written, StringComparison.Ordinal);
    }

    [Fact]
    public void New_group_uses_apple_properties_and_round_trips()
    {
        var draft = new GroupDraft("Newsletter, intern", [new GroupMember("lea", null), new GroupMember("marco", "m@example.com"), new GroupMember(null, "x@example.org")]);

        var written = draft.ToVCard(null);
        var card = VCardReader.Read(written);

        Assert.Contains("VERSION:3.0", written, StringComparison.Ordinal);
        Assert.Contains("X-ADDRESSBOOKSERVER-KIND:group", written, StringComparison.Ordinal);
        Assert.True(card.IsGroup);
        Assert.Equal("Newsletter, intern", card.DisplayName);
        Assert.Equal(draft.Members, card.Members);
    }

    [Theory]
    [InlineData("PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQ", "image/jpeg")]
    [InlineData("PHOTO;ENCODING=BASE64;TYPE=PNG:/9j/4AAQ", "image/png")]
    [InlineData("PHOTO:data:image/jpeg;base64,/9j/4AAQ", "image/jpeg")]
    public void Embedded_photos_are_read(string line, string mediaType)
    {
        var card = VCardReader.Read($"BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Anna\r\n{line}\r\nEND:VCARD\r\n");

        Assert.Equal(mediaType, card.Photo?.MediaType);
        Assert.Equal(Convert.FromBase64String("/9j/4AAQ"), card.Photo?.Data);
    }

    [Fact]
    public void Photo_is_kept_unless_replaced_and_written_per_version()
    {
        const string withLink = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:a\r\nFN:Anna\r\nPHOTO;VALUE=uri:https://example.com/anna.jpg\r\nEND:VCARD\r\n";
        var draft = ContactDraft.FromVCard(withLink);

        // Not replaced: even a linked picture Neruna cannot show survives an edit.
        Assert.Contains("PHOTO;VALUE=uri:https://example.com/anna.jpg", (draft with { Title = "CEO" }).ToVCard(withLink), StringComparison.Ordinal);

        var picture = new ContactPhoto(Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), "image/jpeg");
        var v3 = (draft with { Photo = picture, ReplacePhoto = true }).ToVCard(withLink);
        Assert.DoesNotContain("example.com/anna.jpg", v3, StringComparison.Ordinal);
        Assert.Contains("PHOTO;ENCODING=b;TYPE=JPEG:", v3, StringComparison.Ordinal);
        Assert.Equal(picture.Data, VCardReader.Read(v3).Photo?.Data);

        var v4 = (ContactDraft.Empty with { GivenName = "Beat", Photo = picture, ReplacePhoto = true }).ToVCard("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:x\r\nEND:VCARD\r\n");
        Assert.Contains("PHOTO:data:image/jpeg;base64,", v4, StringComparison.Ordinal);
        Assert.Equal(picture.Data, VCardReader.Read(v4).Photo?.Data);

        var removed = (ContactDraft.FromVCard(v3) with { Photo = null, ReplacePhoto = true }).ToVCard(v3);
        Assert.DoesNotContain("PHOTO", removed, StringComparison.Ordinal);
    }
}
