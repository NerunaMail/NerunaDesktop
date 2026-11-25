using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public sealed class LinkKindsTests
{
    [Fact]
    public void OtherLinksAreFoundOnlyWhenAsked()
    {
        const string text = "Hinweis:Text siehe https://example.com, zoommtg://zoom.us/join?confno=1 oder mailto:anna@example.com und tel:+41417550000.";
        Assert.Single(MessageContent.FindLinkRanges(text));
        var all = MessageContent.FindLinkRanges(text, otherSchemes: true).Select(r => r.Uri.Scheme).ToList();
        Assert.Equal(["https", "zoommtg", "mailto", "tel"], all);
    }

    [Fact]
    public void ScriptAndDataLinksAreNeverLinks()
    {
        Assert.Empty(MessageContent.FindLinkRanges("javascript://alert(1) data://x ms-msdt://id", otherSchemes: true));
        Assert.True(LinkKinds.IsBlocked(new Uri("search-ms:query=x")));
    }

    [Fact]
    public void OnlyProgramLinksCanBeTrusted()
    {
        Assert.True(LinkKinds.CanTrust(new Uri("zoommtg://zoom.us/join")));
        Assert.False(LinkKinds.CanTrust(new Uri("file:///tmp/a.txt")));
        Assert.Equal(LinkKind.Mail, LinkKinds.Of(new Uri("mailto:anna@example.com")));
        Assert.Equal(LinkKind.Web, LinkKinds.Of(new Uri("https://example.com")));
    }
}
