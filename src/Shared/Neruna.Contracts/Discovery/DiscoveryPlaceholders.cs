namespace Neruna.Contracts.Discovery;

/// <summary>Placeholders defined by the Thunderbird autoconfig format, used for usernames.</summary>
public static class DiscoveryPlaceholders
{
    public const string EmailAddress = "%EMAILADDRESS%";
    public const string EmailLocalPart = "%EMAILLOCALPART%";
    public const string EmailDomain = "%EMAILDOMAIN%";

    public static string Expand(string template, string emailAddress)
    {
        ArgumentNullException.ThrowIfNull(template);
        var (localPart, domain) = SplitAddress(emailAddress);

        return template
            .Replace(EmailAddress, emailAddress, StringComparison.Ordinal)
            .Replace(EmailLocalPart, localPart, StringComparison.Ordinal)
            .Replace(EmailDomain, domain, StringComparison.Ordinal);
    }

    public static (string LocalPart, string Domain) SplitAddress(string emailAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailAddress);
        var at = emailAddress.LastIndexOf('@');
        if (at <= 0 || at == emailAddress.Length - 1)
        {
            throw new FormatException($"'{emailAddress}' is not a valid email address.");
        }

        return (emailAddress[..at], emailAddress[(at + 1)..].ToLowerInvariant());
    }
}
