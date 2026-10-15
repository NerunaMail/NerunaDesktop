using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace Neruna.Core.Privacy;

/// <summary>A link in a mail: where it goes, what it shows and what appears when the mouse is over it.</summary>
/// <param name="Hidden">Inside an element the reader cannot see (display: none, font size 0 …).</param>
public sealed record HtmlLink(string Href, string Text, string? Title, bool Hidden);

/// <summary>
/// What the reader of an HTML mail cannot see at first glance: the links with their texts and hover texts, and text
/// that is there but hidden. Reads the original HTML, before it is cleaned up for display.
/// </summary>
public sealed partial class HtmlScan
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> NoText = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "script", "head", "title", "template", "noscript",
    };

    private HtmlScan(IReadOnlyList<HtmlLink> links, string hiddenText)
    {
        Links = links;
        HiddenText = hiddenText;
    }

    public IReadOnlyList<HtmlLink> Links { get; }

    /// <summary>The hidden text, whitespace collapsed (newsletters often hide a short preview line – that is common).</summary>
    public string HiddenText { get; }

    public static HtmlScan Of(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var links = new List<HtmlLink>();
        var hidden = new StringBuilder();
        var open = new List<(string Name, bool Hidden, bool NoText)>();
        (string Href, string? Title, bool Hidden, StringBuilder Text)? anchor = null;

        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag when tag.IsEndTag:
                    var index = open.FindLastIndex(o => o.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        open.RemoveRange(index, open.Count - index);
                    }

                    if (tag.Name.Equals("a", StringComparison.OrdinalIgnoreCase) && anchor is { } done)
                    {
                        links.Add(new HtmlLink(done.Href, Collapse(done.Text.ToString()), done.Title, done.Hidden));
                        anchor = null;
                    }

                    break;

                case HtmlTagToken tag:
                    var isHidden = (open.Count > 0 && open[^1].Hidden) || IsHidden(tag);
                    var noText = (open.Count > 0 && open[^1].NoText) || NoText.Contains(tag.Name);
                    if (tag.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
                    {
                        if (anchor is { } unclosed)
                        {
                            links.Add(new HtmlLink(unclosed.Href, Collapse(unclosed.Text.ToString()), unclosed.Title, unclosed.Hidden));
                        }

                        anchor = Attribute(tag, "href") is { Length: > 0 } href
                            ? (href.Trim(), Attribute(tag, "title"), isHidden, new StringBuilder())
                            : null;
                    }
                    else if (tag.Name.Equals("img", StringComparison.OrdinalIgnoreCase) && anchor is { } inImage && Attribute(tag, "alt") is { Length: > 0 } alt)
                    {
                        inImage.Text.Append(' ').Append(alt).Append(' ');
                    }

                    if (!VoidElements.Contains(tag.Name) && !tag.IsEmptyElement)
                    {
                        open.Add((tag.Name, isHidden, noText));
                    }

                    break;

                case HtmlDataToken data when open.Count == 0 || !open[^1].NoText:
                    anchor?.Text.Append(data.Data);
                    if (open.Count > 0 && open[^1].Hidden)
                    {
                        hidden.Append(data.Data).Append(' ');
                    }

                    break;
            }
        }

        if (anchor is { } last)
        {
            links.Add(new HtmlLink(last.Href, Collapse(last.Text.ToString()), last.Title, last.Hidden));
        }

        return new HtmlScan(links, Collapse(hidden.ToString()));
    }

    private static string? Attribute(HtmlTagToken tag, string name) =>
        tag.Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    // display:none, visibility:hidden, font-size 0/1px, opacity 0, zero height with hidden overflow, Outlook's mso-hide.
    private static bool IsHidden(HtmlTagToken tag)
    {
        if (tag.Attributes.Any(a => a.Name.Equals("hidden", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var style = Attribute(tag, "style");
        return style is not null && HiddenStyle().IsMatch(style);
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"display\s*:\s*none|visibility\s*:\s*hidden|mso-hide\s*:\s*all|(?<![-\w])font-size\s*:\s*[01](\.0+)?\s*(px|pt|em|rem)?\s*(;|$|!)|(?<![-\w])opacity\s*:\s*0(\.0+)?\s*(;|$|!)|(?<![-\w])(max-)?height\s*:\s*0(px)?\s*(;|$|!)[^""]*overflow\s*:\s*hidden", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenStyle();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
