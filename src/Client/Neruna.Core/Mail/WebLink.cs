using System.Text.RegularExpressions;

namespace Neruna.Core.Mail;

/// <summary>
/// The target typed into "Link einfügen": a web address (also without "https://"), an e-mail address or a phone
/// number. Only http(s), mailto and tel go through – never javascript: or file:.
/// </summary>
public static partial class WebLink
{
    /// <summary>"neruna.org" → "https://neruna.org/", "anna@example.com" → "mailto:anna@example.com"; null when it is no link.</summary>
    public static string? Normalize(string? link)
    {
        var text = link?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (PhoneLink.Normalize(text) is { } phone)
        {
            return phone;
        }

        if (!text.Contains(':', StringComparison.Ordinal))
        {
            if (Email().IsMatch(text))
            {
                text = "mailto:" + text;
            }
            else if (Host().IsMatch(text))
            {
                text = "https://" + text;
            }
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"
            ? uri.AbsoluteUri
            : null;
    }

    [GeneratedRegex(@"^[^\s@/]+@[^\s@/]+\.[^\s@/.]+$")]
    private static partial Regex Email();

    // A host with a dot ("neruna.org", "www.neruna.org/preise"), no spaces.
    [GeneratedRegex(@"^[\p{L}\p{N}-]+(\.[\p{L}\p{N}-]+)+(/\S*)?$")]
    private static partial Regex Host();
}
