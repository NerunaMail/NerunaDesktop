namespace Neruna.Core.Discovery;

/// <summary>Providers whose accounts are set up their own way (sign-in in the browser, the Bridge) instead of IMAP.</summary>
public enum MailHost
{
    /// <summary>Nothing special: IMAP/SMTP, CalDAV and CardDAV are looked up as usual.</summary>
    Other,

    /// <summary>Microsoft 365 or Outlook.com: sign-in at Microsoft.</summary>
    Microsoft,

    /// <summary>Gmail or Google Workspace: sign-in at Google.</summary>
    Google,

    /// <summary>Proton Mail: only through the Proton Mail Bridge on the computer.</summary>
    Proton,
}

/// <summary>
/// "Konto hinzufügen" with the address first: which provider an address belongs to – from its domain (Outlook.com,
/// Gmail …) or, for an own domain, from the servers that take its mail (MX).
/// </summary>
public static class MailHostRecognition
{
    private static readonly HashSet<string> MicrosoftDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook.com", "outlook.de", "outlook.fr", "outlook.it", "outlook.at", "outlook.es", "outlook.co.uk",
        "hotmail.com", "hotmail.de", "hotmail.fr", "hotmail.it", "hotmail.es", "hotmail.co.uk",
        "live.com", "live.de", "live.fr", "live.it", "live.at", "live.co.uk", "msn.com", "passport.com",
    };

    private static readonly HashSet<string> GoogleDomains = new(StringComparer.OrdinalIgnoreCase) { "gmail.com", "googlemail.com" };

    private static readonly HashSet<string> ProtonDomains = new(StringComparer.OrdinalIgnoreCase) { "proton.me", "protonmail.com", "protonmail.ch", "pm.me" };

    /// <summary>From the domain alone (no network); <see cref="MailHost.Other"/> when it is not one of the known ones.</summary>
    public static MailHost ByDomain(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var name = domain.Trim().TrimEnd('.');
        return MicrosoftDomains.Contains(name) ? MailHost.Microsoft
            : GoogleDomains.Contains(name) ? MailHost.Google
            : ProtonDomains.Contains(name) ? MailHost.Proton
            : MailHost.Other;
    }

    /// <summary>From the mail servers (MX) of an own domain, e.g. <c>example-ch.mail.protection.outlook.com</c>.</summary>
    public static MailHost ByMailExchangers(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        foreach (var raw in hosts)
        {
            var host = raw.Trim().TrimEnd('.');
            if (EndsWith(host, "mail.protection.outlook.com") || EndsWith(host, "outlook.com") || EndsWith(host, "hotmail.com"))
            {
                return MailHost.Microsoft;
            }

            if (EndsWith(host, "google.com") || EndsWith(host, "googlemail.com"))
            {
                return MailHost.Google;
            }

            if (EndsWith(host, "protonmail.ch") || EndsWith(host, "proton.me"))
            {
                return MailHost.Proton;
            }
        }

        return MailHost.Other;
    }

    private static bool EndsWith(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
}
