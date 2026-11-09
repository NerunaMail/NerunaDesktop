using Neruna.Core.Contacts;

namespace Neruna.Client.Tests;

public sealed class RecipientFieldTests
{
    [Fact]
    public void A_list_as_pasted_becomes_one_address_each()
    {
        var (addresses, rest) = RecipientField.Split(
            "Jes Caspersen <JCaspersen@itcag.com>, Naireta Chatterjee <nchatterjee@itcag.com>; csekhar@itcag.com, 'Ender Informatics GmbH' <info@ender-informatics.ch>",
            all: true);

        Assert.Equal(["JCaspersen@itcag.com", "nchatterjee@itcag.com", "csekhar@itcag.com", "info@ender-informatics.ch"], addresses.Select(a => a.Mailbox.Address));
        Assert.Equal("Jes Caspersen", addresses[0].Mailbox.Name);
        Assert.Equal("csekhar@itcag.com", addresses[2].Raw);
        Assert.Equal(string.Empty, rest);
    }

    [Fact]
    public void A_comma_inside_quotes_or_angle_brackets_does_not_split()
    {
        var (addresses, rest) = RecipientField.Split("\"Muster, Anna\" <anna@example.com>, b@example.com", all: true);

        Assert.Equal(2, addresses.Count);
        Assert.Equal("Muster, Anna", addresses[0].Mailbox.Name);
        Assert.Equal(string.Empty, rest);
    }

    [Fact]
    public void While_typing_the_last_part_stays_text()
    {
        var (addresses, rest) = RecipientField.Split("anna@example.com, mar", all: false);

        Assert.Single(addresses);
        Assert.Equal("mar", rest);
    }

    [Fact]
    public void An_incomplete_address_stays_text_with_everything_after_it()
    {
        var (addresses, rest) = RecipientField.Split("anna@example.com, marco, lea@example.com", all: true);

        Assert.Single(addresses);
        Assert.Equal("marco, lea@example.com", rest);
        Assert.Null(RecipientField.AsMailbox("anna@"));
        Assert.Null(RecipientField.AsMailbox("Anna Muster"));
    }
}
