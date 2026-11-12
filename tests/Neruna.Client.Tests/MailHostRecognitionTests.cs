using Neruna.Core.Discovery;

namespace Neruna.Client.Tests;

public class MailHostRecognitionTests
{
    [Theory]
    [InlineData("outlook.com", MailHost.Microsoft)]
    [InlineData("Hotmail.de", MailHost.Microsoft)]
    [InlineData("gmail.com", MailHost.Google)]
    [InlineData("googlemail.com", MailHost.Google)]
    [InlineData("proton.me", MailHost.Proton)]
    [InlineData("pm.me", MailHost.Proton)]
    [InlineData("example.com", MailHost.Other)]
    [InlineData("outlook.example.com", MailHost.Other)]
    public void Known_domains_tell_the_provider(string domain, MailHost expected) =>
        Assert.Equal(expected, MailHostRecognition.ByDomain(domain));

    [Theory]
    [InlineData("muster-ch.mail.protection.outlook.com.", MailHost.Microsoft)]
    [InlineData("aspmx.l.google.com", MailHost.Google)]
    [InlineData("smtp.google.com", MailHost.Google)]
    [InlineData("mail.protonmail.ch", MailHost.Proton)]
    [InlineData("mx01.mail.example.com", MailHost.Other)]
    [InlineData("google.com.example.net", MailHost.Other)]
    [InlineData("notgoogle.com", MailHost.Other)]
    public void Mail_servers_of_an_own_domain_tell_the_provider(string mx, MailHost expected) =>
        Assert.Equal(expected, MailHostRecognition.ByMailExchangers([mx]));

    [Fact]
    public void No_mail_servers_is_an_ordinary_account() =>
        Assert.Equal(MailHost.Other, MailHostRecognition.ByMailExchangers([]));
}
