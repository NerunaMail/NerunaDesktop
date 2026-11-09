using MimeKit;

namespace Neruna.Core.Contacts;

/// <summary>The text of an An/Cc/Bcc field: which parts are complete addresses (shown as boxes) and what is still typed.</summary>
public static class RecipientField
{
    /// <summary>
    /// Splits <paramref name="text"/> at commas and semicolons outside quotes and angle brackets. Leading complete
    /// addresses come back as boxes; from the first one that is not a complete address on, the text stays as typed.
    /// The last part counts only with <paramref name="all"/> (while typing it may still grow).
    /// </summary>
    public static (List<(string Raw, MailboxAddress Mailbox)> Addresses, string Remainder) Split(string text, bool all)
    {
        var found = new List<(string, MailboxAddress)>();
        var start = 0;
        var quoted = false;
        var angle = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var end = i == text.Length;
            if (!end)
            {
                var c = text[i];
                if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && c == '<')
                {
                    angle++;
                }
                else if (!quoted && c == '>' && angle > 0)
                {
                    angle--;
                }

                if (quoted || angle > 0 || (c != ',' && c != ';'))
                {
                    continue;
                }
            }
            else if (!all)
            {
                break;
            }

            var part = text[start..i].Trim();
            if (part.Length > 0)
            {
                if (AsMailbox(part) is not { } mailbox)
                {
                    break;
                }

                found.Add((part, mailbox));
            }

            start = Math.Min(i + 1, text.Length);
        }

        return (found, text[start..].TrimStart());
    }

    /// <summary>A complete address (with a domain), or null.</summary>
    public static MailboxAddress? AsMailbox(string part) =>
        MailboxAddress.TryParse(part, out var mailbox) && mailbox.Address.IndexOf('@') is > 0 and var at && at < mailbox.Address.Length - 1
            ? mailbox
            : null;
}
