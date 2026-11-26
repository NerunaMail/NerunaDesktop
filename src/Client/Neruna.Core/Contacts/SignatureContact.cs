using System.Text.RegularExpressions;
using MimeKit;
using Neruna.Core.Mail;

namespace Neruna.Core.Contacts;

/// <summary>
/// Reads what a mail's signature tells about its sender – phone numbers, organisation, title, postal and web address –
/// to offer it when the sender is saved as a contact. A guess from the text: shown in the editor to check, never saved
/// on its own.
/// </summary>
public static partial class SignatureContact
{
    private const int MaxLines = 20;

    [GeneratedRegex(@"^\s*(>|(am|on|le|il)\b.{3,200}\b(schrieb|wrote|a écrit|ha scritto)\b|-{2,}\s*(original|ursprüngliche|message d'origine|messaggio originale|weitergeleitete|forwarded)|_{8,}|(von|from|de|da|gesendet|sent)\s*:\s+\S)", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteStart();

    [GeneratedRegex(@"^\s*((freundliche|beste|liebe|herzliche|viele|sonnige)\s+)?(gr(ü|ue)(ss|ß)e|gruss|gruß)\b|^\s*mit\s+(freundlichen|besten|lieben)|^\s*(best|kind|warm)\s+regards|^\s*regards\b|^\s*cheers\b|^\s*(cordialement|bien\s+(à\s+(vous|toi)|cordialement)|meilleures\s+salutations|salutations)|^\s*(cordiali|distinti|cari|un\s+caro)?\s*salut(i|o)\b|^\s*(thanks|thank\s+you|danke|merci|grazie)\b.{0,20}$", RegexOptions.IgnoreCase)]
    private static partial Regex Greeting();

    [GeneratedRegex(@"(?<![\w.@/])(?:\+|00)?\d[\d  ().\/-]{6,}\d(?![\w@])")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"\b(mobil|mobile|natel|handy|cell|portable|cellulare|gsm)\b|^\s*m\s*[:.]|^\s*m\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MobileLabel();

    [GeneratedRegex(@"\bfax\b|^\s*f\s*[:.]|^\s*f\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FaxLabel();

    [GeneratedRegex(@"\b(iban|bic|swift|uid|mwst|mwst-nr|vat|che-\d|ust|konto|account|postkonto|pc-konto|clearing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAPhoneLine();

    [GeneratedRegex(@"\b(https?://[^\s<>()""']+|www\.[^\s<>()""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"\b(linkedin|facebook|twitter|instagram|xing|youtube|tiktok|x\.com|threads|mastodon|wa\.me|t\.me|maps\.|goo\.gl|bit\.ly|teams\.microsoft|zoom\.us)", RegexOptions.IgnoreCase)]
    private static partial Regex SocialSite();

    [GeneratedRegex(@"^(?:(?:CH|D|DE|A|AT|F|FR|I|IT|FL|LI)[- ])?(\d{4,5})\s+(\p{L}[\p{L} .'’()-]{1,40}?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PostalLine();

    [GeneratedRegex(@"^(\p{L}[\p{L} .'’-]{2,40}?)\s+\d{1,4}\s*[a-z]?$|^\d{1,4}\s*[a-z]?,?\s+\p{L}[\p{L} .'’-]{2,40}$|\b(strasse|straße|str\.|weg|gasse|platz|allee|ring|rain|halde|matte|rue|avenue|av\.|chemin|ch\.|route|via|viale|piazza|corso|road|street|lane)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StreetLine();

    [GeneratedRegex(@"\b(AG|GmbH|SA|Sàrl|SARL|Sagl|Ltd\.?|Inc\.?|LLC|KG|OHG|e\.\s?V\.|S\.p\.A\.|SpA|S\.r\.l\.|Srl|SRL|BV|NV|plc|Genossenschaft|Stiftung|Verein|Treuhand|Holding)(?![\p{L}])", RegexOptions.None)]
    private static partial Regex LegalForm();

    [GeneratedRegex(@"\s*(?:[|·•]|\s{3,}|,\s+(?=\D*\d{4,5}\s))\s*")]
    private static partial Regex Separators();

    private static readonly HashSet<string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Schweiz", "Switzerland", "Suisse", "Svizzera", "Svizra", "Deutschland", "Germany", "Allemagne", "Germania",
        "Österreich", "Austria", "Autriche", "Liechtenstein", "Frankreich", "France", "Francia", "Italien", "Italy", "Italie",
        "Italia", "Belgien", "Belgium", "Belgique", "Niederlande", "Netherlands", "Luxemburg", "Luxembourg", "Spanien", "Spain",
        "España", "United Kingdom", "UK", "USA", "United States",
    };

    /// <summary>What the signature of <paramref name="message"/> shows about <paramref name="sender"/>; empty when nothing.</summary>
    public static ContactDraft Read(MimeMessage message, string? senderName, string? senderAddress)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = !string.IsNullOrWhiteSpace(message.TextBody) ? message.TextBody
            : message.HtmlBody is { } html ? HtmlText.ToPlainText(html)
            : string.Empty;
        return ReadText(text, senderName, senderAddress);
    }

    /// <summary>The same from the text of a mail (signature at the end of the own part, before any quoted mail).</summary>
    public static ContactDraft ReadText(string body, string? senderName, string? senderAddress)
    {
        ArgumentNullException.ThrowIfNull(body);
        var block = SignatureBlock(body, senderName);
        if (block.Count == 0)
        {
            return ContactDraft.Empty;
        }

        // A line with "Street 1 | 8000 City | +41 …" is read in its parts.
        var parts = block.SelectMany(line => Separators().Split(line)).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        var phones = new List<ContactField>();
        foreach (var line in block.Where(l => !NotAPhoneLine().IsMatch(l) && !l.Contains('@', StringComparison.Ordinal)))
        {
            foreach (var segment in Separators().Split(line))
            {
                foreach (Match match in PhoneNumber().Matches(segment))
                {
                    var number = match.Value.Trim();
                    var digits = number.Count(char.IsAsciiDigit);
                    if (digits is < 9 or > 15 || LooksLikeDate(number) || phones.Any(p => ContactDraft.SamePhone(p.Value, number)))
                    {
                        continue;
                    }

                    var label = segment[..match.Index];
                    var kind = MobileLabel().IsMatch(label) ? "cell" : FaxLabel().IsMatch(label) ? "fax" : "work";
                    phones.Add(new ContactField(number.Replace(' ', ' '), kind));
                }
            }
        }

        var domain = senderAddress is { } a && a.LastIndexOf('@') is var at and >= 0 ? a[(at + 1)..].ToLowerInvariant() : null;
        var sites = block.SelectMany(l => WebAddress().Matches(l).Select(m => m.Value.TrimEnd('.', ',', ';', ':', ')', '/')))
            .Where(u => !SocialSite().IsMatch(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var site = sites.FirstOrDefault(u => domain is not null && u.Contains(BaseDomain(domain), StringComparison.OrdinalIgnoreCase)) ?? sites.FirstOrDefault();

        var organization = parts.FirstOrDefault(p => LegalForm().IsMatch(p) && !p.Contains('@', StringComparison.Ordinal)
            && p.Count(char.IsAsciiDigit) < 3 && p.Length <= 60 && !Greeting().IsMatch(p));

        var address = FindAddress(parts);
        var title = FindTitle(block, senderName, organization);

        return ContactDraft.Empty with
        {
            Organization = organization,
            Title = title,
            Phones = phones,
            Addresses = address is null ? null : [address],
            Websites = site is null ? null : [new ContactField(site.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + site : site, "work")],
        };
    }

    /// <summary>True when nothing was found.</summary>
    public static bool IsEmpty(ContactDraft found)
    {
        ArgumentNullException.ThrowIfNull(found);
        return found.Phones.Count == 0 && string.IsNullOrEmpty(found.Organization) && string.IsNullOrEmpty(found.Title)
            && found.Addresses is not { Count: > 0 } && found.Websites is not { Count: > 0 };
    }

    // The own part of the mail (before a quoted one), from "-- ", a greeting or the sender's name on: the last lines.
    private static List<string> SignatureBlock(string body, string? senderName)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        var end = lines.FindIndex(l => QuoteStart().IsMatch(l));
        if (end >= 0)
        {
            lines = lines[..end];
        }

        var start = lines.FindLastIndex(l => l is "-- " or "--");
        if (start < 0)
        {
            start = lines.FindLastIndex(l => Greeting().IsMatch(l));
        }

        if (start < 0 && NameWords(senderName) is { Count: > 0 } words)
        {
            start = lines.FindLastIndex(l => words.All(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        if (start < 0)
        {
            start = Math.Max(0, lines.Count - 12);
        }

        return [.. lines.Skip(start).Select(l => l.Trim()).Where(l => l.Length is > 0 and <= 120).Take(MaxLines)];
    }

    private static ContactAddress? FindAddress(List<string> parts)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var country = (string?)null;
            var comma = part.LastIndexOf(',');
            if (comma > 0 && Countries.Contains(part[(comma + 1)..].Trim()))
            {
                country = part[(comma + 1)..].Trim();
                part = part[..comma].Trim();
            }

            if (PostalLine().Match(part) is not { Success: true } postal || Countries.Contains(postal.Groups[2].Value.Trim()))
            {
                continue;
            }

            var street = i > 0 && StreetLine().IsMatch(parts[i - 1]) && !parts[i - 1].Contains('@', StringComparison.Ordinal)
                && !LegalForm().IsMatch(parts[i - 1]) ? parts[i - 1] : null;
            if (country is null && i + 1 < parts.Count && Countries.Contains(parts[i + 1]))
            {
                country = parts[i + 1];
            }

            return new ContactAddress(street, postal.Groups[1].Value, postal.Groups[2].Value.Trim(), country, "work");
        }

        return null;
    }

    // The line after the sender's name, when it is a short text: "Geschäftsführer", "Leiterin Buchhaltung".
    private static string? FindTitle(List<string> block, string? senderName, string? organization)
    {
        var words = NameWords(senderName);
        if (words.Count == 0)
        {
            return null;
        }

        var nameLine = block.FindIndex(l => words.All(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)) && l.Length <= words.Sum(w => w.Length) + 25);
        if (nameLine < 0 || nameLine + 1 >= block.Count)
        {
            return null;
        }

        var next = block[nameLine + 1];
        // A title stands alone on its line: no digits, no address, no web link, no organisation, not too long.
        return next.Length is >= 3 and <= 50 && !next.Any(char.IsAsciiDigit) && !next.Contains('@', StringComparison.Ordinal)
               && !WebAddress().IsMatch(next) && next != organization && !LegalForm().IsMatch(next) && !Greeting().IsMatch(next)
               && !Separators().IsMatch(next)
            ? next
            : null;
    }

    private static List<string> NameWords(string? name) =>
        [.. (name ?? string.Empty).Split([' ', ',', '"', '\''], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2 && !w.Contains('@', StringComparison.Ordinal))];

    // "12.03.2026", "2026-03-12": no phone numbers.
    private static bool LooksLikeDate(string value) =>
        Regex.IsMatch(value, @"^\d{1,2}\.\d{1,2}\.\d{2,4}$|^\d{4}-\d{2}-\d{2}$");

    private static string BaseDomain(string domain)
    {
        var parts = domain.Split('.');
        return parts.Length >= 2 ? string.Join('.', parts[^2..]) : domain;
    }
}
