using System.Net;
using System.Text.RegularExpressions;
using Neruna.Core.Mail;

namespace Neruna.Core.Notes;

/// <summary>Title, preview and searchable text of a note's HTML.</summary>
public static partial class NoteText
{
    private const int TitleLength = 120;
    private const int PreviewLength = 200;

    public static string PlainText(string html) => string.IsNullOrEmpty(html) ? string.Empty : HtmlText.ToPlainText(html);

    /// <summary>The first line with text (as in the notes apps of phones and Nextcloud); empty for an empty note.</summary>
    public static string Title(string html) => Shorten(Lines(PlainText(html)).FirstOrDefault() ?? string.Empty, TitleLength);

    /// <summary>The lines after the title, joined.</summary>
    public static string Preview(string html) => Shorten(string.Join(" ", Lines(PlainText(html)).Skip(1)), PreviewLength);

    /// <summary>A note starting with <paramref name="title"/> as its first line (a heading).</summary>
    public static string WithTitle(string title, string bodyHtml) =>
        $"<h2>{WebUtility.HtmlEncode(title)}</h2>{bodyHtml}";

    /// <summary>Plain text as note HTML: one line per paragraph line, links kept as text.</summary>
    public static string FromPlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(text.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Length == 0 ? "<div><br></div>" : $"<div>{WebUtility.HtmlEncode(line)}</div>"));
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    /// <summary>Email addresses written in the text (they tie the note to a contact like a link does).</summary>
    public static IEnumerable<string> Addresses(string text) =>
        AddressPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal);

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex AddressPattern();
}
