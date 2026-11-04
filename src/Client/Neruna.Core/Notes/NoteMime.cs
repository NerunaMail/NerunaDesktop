using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace Neruna.Core.Notes;

/// <summary>
/// A note as a message in a mailbox, in the format the notes apps of iPhone and Mac (and e.g. ImapNotes3 on Android)
/// use for mail accounts: a message in the "Notes" folder, HTML body, the X-Uniform-Type-Identifier header
/// "com.apple.mail-note" and the note's id in X-Universally-Unique-Identifier. The subject is the title (first line).
/// It is never sent – only stored with IMAP APPEND.
/// </summary>
public static partial class NoteMime
{
    public const string TypeHeader = "X-Uniform-Type-Identifier";
    public const string TypeValue = "com.apple.mail-note";
    public const string IdHeader = "X-Universally-Unique-Identifier";
    public const string CreatedHeader = "X-Mail-Created-Date";

    /// <param name="from">The mailbox's own address (as the phone apps write it).</param>
    public static MimeMessage ToMime(Note note, string? from)
    {
        ArgumentNullException.ThrowIfNull(note);
        var message = new MimeMessage
        {
            Subject = note.Title,
            Date = note.Modified,
            MessageId = MimeUtils.GenerateMessageId("neruna.invalid"),
        };
        message.From.Add(string.IsNullOrWhiteSpace(from) ? new MailboxAddress("Neruna", "notes@neruna.invalid") : MailboxAddress.Parse(from));
        message.Headers.Add(TypeHeader, TypeValue);
        message.Headers.Add(IdHeader, note.Id);
        message.Headers.Add(CreatedHeader, DateUtils.FormatDate(note.Created));
        message.Body = new TextPart("html")
        {
            Text = "<html><head><meta charset=\"utf-8\"></head><body>" + note.Html + "</body></html>",
        };
        return message;
    }

    /// <summary>The note in a message of a notes folder (also one another app wrote, maybe without our headers).</summary>
    public static Note FromMime(MimeMessage message, Guid connectionId, string notebook)
    {
        ArgumentNullException.ThrowIfNull(message);
        var id = message.Headers[IdHeader] is { Length: > 0 } uuid ? uuid.Trim()
            : message.MessageId is { Length: > 0 } messageId ? messageId.Trim('<', '>')
            : Guid.NewGuid().ToString("D").ToUpperInvariant();

        var html = message.HtmlBody is { } body ? Inner(body)
            : message.TextBody is { } text ? NoteText.FromPlainText(text)
            : string.Empty;

        // Some apps keep the title only in the subject: put it in front, so it is the first line here too.
        if (message.Subject is { Length: > 0 } subject && NoteText.Title(html).Length == 0)
        {
            html = NoteText.WithTitle(subject, html);
        }

        var modified = message.Date == DateTimeOffset.MinValue ? DateTimeOffset.Now : message.Date;
        var created = message.Headers[CreatedHeader] is { } c && DateUtils.TryParse(c, out var parsed) ? parsed : modified;
        return new Note(id, connectionId, notebook, html, created, modified);
    }

    /// <summary>Whether the message says it is a note (other messages in a notes folder are notes too).</summary>
    public static bool IsNote(MimeMessage message) =>
        string.Equals(message?.Headers[TypeHeader]?.Trim(), TypeValue, StringComparison.OrdinalIgnoreCase);

    // What is between <body> and </body>; a fragment as it is.
    private static string Inner(string html)
    {
        var match = BodyPattern().Match(html);
        var inner = match.Success ? match.Groups[1].Value : html;
        return HeadPattern().Replace(inner, string.Empty).Trim();
    }

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyPattern();

    [GeneratedRegex(@"<head[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadPattern();
}
