using System.Text;
using MimeKit.Text;

namespace Neruna.Core.Mail;

/// <summary>Turns an HTML body into readable plain text (for previews and until an HTML renderer is integrated).</summary>
public static class HtmlText
{
    // Paragraph-like blocks get an empty line around them …
    private static readonly HashSet<HtmlTagId> ParagraphTags =
    [
        HtmlTagId.P, HtmlTagId.H1, HtmlTagId.H2, HtmlTagId.H3, HtmlTagId.H4, HtmlTagId.H5, HtmlTagId.H6,
        HtmlTagId.BlockQuote, HtmlTagId.Table, HtmlTagId.HR, HtmlTagId.UL, HtmlTagId.OL,
    ];

    // … line-like elements just start a new line (a <div> is a line in common mail editors, including ours).
    private static readonly HashSet<HtmlTagId> LineTags = [HtmlTagId.LI, HtmlTagId.TR];

    public static string ToPlainText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = new StringBuilder();
        var skipDepth = 0;
        var tokenizer = new HtmlTokenizer(new StringReader(html)) { DecodeCharacterReferences = true };

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken { Id: HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Head } tag:
                    skipDepth += tag.IsEndTag ? -1 : tag.IsEmptyElement ? 0 : 1;
                    break;
                case HtmlTagToken tag when skipDepth == 0 && ParagraphTags.Contains(tag.Id):
                    AppendLineBreak(text, paragraph: true);
                    break;
                case HtmlTagToken { IsEndTag: false } tag when skipDepth == 0 && LineTags.Contains(tag.Id):
                    AppendLineBreak(text, paragraph: false);
                    break;
                case HtmlTagToken { Id: HtmlTagId.Div } when skipDepth == 0:
                    AppendLineBreak(text, paragraph: false);
                    break;
                case HtmlTagToken { Id: HtmlTagId.Br, IsEndTag: false } when skipDepth == 0:
                    // Unlike block boundaries, every <br> counts: "<div><br></div>" is an empty line.
                    TrimSpaces(text);
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    break;
                case HtmlDataToken data when skipDepth == 0:
                    AppendCollapsed(text, data.Data);
                    break;
            }
        }

        return text.ToString().Trim();
    }

    private static void AppendCollapsed(StringBuilder text, string data)
    {
        foreach (var c in data)
        {
            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && !char.IsWhiteSpace(text[^1]))
                {
                    text.Append(' ');
                }
            }
            else
            {
                text.Append(c);
            }
        }
    }

    private static void TrimSpaces(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ')
        {
            text.Length--;
        }
    }

    private static void AppendLineBreak(StringBuilder text, bool paragraph)
    {
        TrimSpaces(text);

        if (text.Length == 0)
        {
            return;
        }

        var wanted = paragraph ? 2 : 1;
        var existing = text.Length >= 2 && text[^1] == '\n' && text[^2] == '\n' ? 2 : text[^1] == '\n' ? 1 : 0;
        text.Append('\n', Math.Max(0, wanted - existing));
    }
}
