using System.Text.RegularExpressions;

namespace Neruna.Core.Mail;

/// <summary>
/// A tel: link (RFC 3966), e.g. in a signature: the system hands it to the telephony program (CTI, softphone).
/// Only a number goes through – digits, a leading "+", and the separators people write.
/// </summary>
public static partial class PhoneLink
{
    /// <summary>"tel:+41 41 755 00 00" → "tel:+41417550000"; null when it is no phone number.</summary>
    public static string? Normalize(string? link)
    {
        var text = link?.Trim();
        if (text is null || !text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var number = Uri.UnescapeDataString(text[4..]).Split(';')[0];
        return Number().IsMatch(number) ? "tel:" + Separators().Replace(number, string.Empty) : null;
    }

    [GeneratedRegex(@"^\s*\+?[\d\s().\-/]{3,30}$")]
    private static partial Regex Number();

    [GeneratedRegex(@"[\s().\-/]")]
    private static partial Regex Separators();
}
