using System.Globalization;
using System.Text;

namespace Neruna.Core.Privacy;

/// <summary>A company often imitated in phishing: the words of its name and the domains its mail comes from.</summary>
/// <param name="Domains">Registrable domains; "*" stands for any part ("sparkasse*.de" fits "sparkasse-koelnbonn.de").</param>
public sealed record Brand(string Name, IReadOnlyList<string> Words, IReadOnlyList<string> Domains);

/// <summary>
/// Banks, post, shops and services whose names phishing mails put in the sender's name. A name that claims one of them
/// with an address of another domain is a strong sign of deception – the classic "Raiffeisen &lt;raiffeisen4@…de&gt;".
/// </summary>
public static class Brands
{
    public static IReadOnlyList<Brand> All { get; } =
    [
        new("Raiffeisen", ["raiffeisen"], ["raiffeisen.ch", "raiffeisen.de", "raiffeisen.at", "raiffeisen.it", "rbinternational.com", "raiffeisen*.de", "raiffeisen*.at"]),
        new("PostFinance", ["postfinance"], ["postfinance.ch"]),
        new("Die Post", ["die post", "swiss post", "schweizerische post", "la poste suisse", "posta svizzera"], ["post.ch", "swisspost.ch"]),
        new("UBS", ["ubs"], ["ubs.com", "ubs.ch"]),
        new("Credit Suisse", ["credit suisse"], ["credit-suisse.com", "ubs.com"]),
        new("Zürcher Kantonalbank", ["zürcher kantonalbank", "zkb"], ["zkb.ch"]),
        new("TWINT", ["twint"], ["twint.ch"]),
        new("Swisscom", ["swisscom"], ["swisscom.ch", "swisscom.com", "bluewin.ch"]),
        new("Sunrise", ["sunrise"], ["sunrise.ch", "sunrise.net"]),
        new("SBB", ["sbb", "sbb cff ffs"], ["sbb.ch"]),
        new("Viseca", ["viseca", "one by viseca"], ["viseca.ch"]),
        new("Swiss Bankers", ["swiss bankers"], ["swissbankers.ch"]),
        new("Sparkasse", ["sparkasse"], ["sparkasse.de", "sparkasse*.de", "sparkasse.at", "s-*.de"]),
        new("Volksbank", ["volksbank"], ["volksbank*.de", "vr.de", "volksbank*.at"]),
        new("Postbank", ["postbank"], ["postbank.de"]),
        new("Deutsche Bank", ["deutsche bank"], ["db.com", "deutsche-bank.de"]),
        new("Commerzbank", ["commerzbank"], ["commerzbank.de", "commerzbank.com"]),
        new("ING", ["ing diba", "ing-diba"], ["ing.de", "ing.com"]),
        new("PayPal", ["paypal"], ["paypal.com", "paypal.ch", "paypal.de", "paypal.fr", "paypal.it", "paypal.at"]),
        new("Amazon", ["amazon"], ["amazon.com", "amazon.de", "amazon.fr", "amazon.it", "amazon.co.uk", "amazon.es", "amazon.nl", "amazonses.com", "amazon.ch"]),
        new("Microsoft", ["microsoft", "office 365", "microsoft 365", "outlook team", "onedrive", "sharepoint"], ["microsoft.com", "office.com", "office365.com", "outlook.com", "live.com", "microsoftonline.com", "sharepoint.com", "onedrive.com"]),
        new("Apple", ["apple", "icloud", "itunes"], ["apple.com", "icloud.com", "itunes.com", "me.com"]),
        new("Google", ["google"], ["google.com", "googlemail.com", "gmail.com", "youtube.com"]),
        new("Netflix", ["netflix"], ["netflix.com"]),
        new("DHL", ["dhl"], ["dhl.com", "dhl.de", "dhl.ch", "dhl.at", "dpdhl.com", "deutschepost.de"]),
        new("DPD", ["dpd"], ["dpd.ch", "dpd.de", "dpd.com", "dpd.at"]),
        new("UPS", ["ups"], ["ups.com"]),
        new("FedEx", ["fedex"], ["fedex.com"]),
        new("Booking.com", ["booking.com"], ["booking.com"]),
        new("Airbnb", ["airbnb"], ["airbnb.com", "airbnb.ch", "airbnb.de"]),
        new("eBay", ["ebay"], ["ebay.com", "ebay.de", "ebay.ch", "ebay.fr", "ebay.it", "ebay.at"]),
        new("Visa", ["visa"], ["visa.com", "visa.ch", "visaeurope.com"]),
        new("Mastercard", ["mastercard"], ["mastercard.com", "mastercard.ch"]),
        new("Steuerverwaltung", ["eidgenössische steuerverwaltung", "estv"], ["admin.ch"]),
    ];

    /// <summary>The brand the display name claims (a whole word of it, upper or lower case, with or without accents), or null.</summary>
    public static Brand? Claimed(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var name = " " + Plain(displayName) + " ";
        return All.FirstOrDefault(b => b.Words.Any(w => name.Contains(" " + Plain(w) + " ", StringComparison.Ordinal)));
    }

    /// <summary>Whether the domain is one of the brand's (its registrable part, with the "*" patterns).</summary>
    public static bool Owns(Brand brand, string domain)
    {
        ArgumentNullException.ThrowIfNull(brand);
        var registrable = DomainNames.Registrable(domain) ?? domain.ToLowerInvariant();
        return brand.Domains.Any(d => d.Contains('*', StringComparison.Ordinal) ? Matches(registrable, d) : registrable == d);
    }

    private static bool Matches(string domain, string pattern)
    {
        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        return domain.Length > pattern.Length - 1 && domain.StartsWith(pattern[..star], StringComparison.Ordinal)
            && domain.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal);
    }

    // Lower case, without accents, everything but letters and digits as a space: "Raiffeisen-Kunden|service" → "raiffeisen kunden service".
    private static string Plain(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) || c == '.' ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
