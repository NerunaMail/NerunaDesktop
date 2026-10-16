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

    /// <summary>For input from a form: false for an empty or invalid address instead of an exception.</summary>
    public static bool TrySplitAddress(string? emailAddress, out string localPart, out string domain)
    {
        localPart = domain = string.Empty;
        var text = emailAddress?.Trim() ?? string.Empty;
        var at = text.LastIndexOf('@');
        if (at <= 0 || at == text.Length - 1)
        {
            return false;
        }

        (localPart, domain) = (text[..at], text[(at + 1)..].ToLowerInvariant());
        return true;
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
