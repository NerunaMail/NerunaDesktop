namespace Neruna.Core.Mail;

/// <summary>
/// A mailto: link (RFC 6068) as a new message: recipients, cc, bcc, subject and body; other header fields are ignored.
/// </summary>
public static class MailtoLink
{
    public static bool IsMailto(string? text) => text?.TrimStart().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) == true;

    public static ComposeDraft Parse(string link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var text = link.Trim();
        if (!IsMailto(text))
        {
            throw new ArgumentException("Not a mailto: link.", nameof(link));
        }

        text = text["mailto:".Length..];
        var query = text.IndexOf('?', StringComparison.Ordinal);
        var to = new List<string>();
        var cc = new List<string>();
        var bcc = new List<string>();
        string subject = string.Empty, body = string.Empty;
        AddAddresses(to, query < 0 ? text : text[..query]);

        if (query >= 0)
        {
            foreach (var pair in text[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                var name = Decode(equals < 0 ? pair : pair[..equals]).Trim().ToLowerInvariant();
                var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
                switch (name)
                {
                    case "to": AddAddresses(to, value); break;
                    case "cc": AddAddresses(cc, value); break;
                    case "bcc": AddAddresses(bcc, value); break;
                    case "subject": subject = Decode(value); break;
                    case "body": body = Decode(value).Replace("\r\n", "\n", StringComparison.Ordinal); break;
                }
            }
        }

        return ComposeDraft.Empty with { To = string.Join("; ", to), Cc = string.Join("; ", cc), Subject = subject, Body = body, Bcc = string.Join("; ", bcc) };
    }

    private static void AddAddresses(List<string> target, string value) =>
        target.AddRange(Decode(value).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // Percent-decoding only: in mailto "+" is a plus sign (e.g. anna+news@example.com), not a space.
    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
