using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

/// <summary>"Link einfügen": the field starts empty, so the https:// is added where it is missing.</summary>
public class WebLinkTests
{
    [Theory]
    [InlineData("https://neruna.org", "https://neruna.org/")]
    [InlineData("  neruna.org ", "https://neruna.org/")]
    [InlineData("www.neruna.org/de/preise", "https://www.neruna.org/de/preise")]
    [InlineData("http://intranet.example.com/x", "http://intranet.example.com/x")]
    [InlineData("anna@example.com", "mailto:anna@example.com")]
    [InlineData("mailto:anna@example.com", "mailto:anna@example.com")]
    [InlineData("tel:+41 41 755 00 00", "tel:+41417550000")]
    public void Addresses_become_links(string typed, string expected) => Assert.Equal(expected, WebLink.Normalize(typed));

    [Theory]
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("nur ein Wort")]
    [InlineData("Hallo")]
    public void Anything_else_is_no_link(string typed) => Assert.Null(WebLink.Normalize(typed));
}
