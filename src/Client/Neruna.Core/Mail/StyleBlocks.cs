using System.Text;
using System.Text.RegularExpressions;

namespace Neruna.Core.Mail;

/// <summary>Makes a mail's own &lt;style&gt; blocks safe for the HTML renderer.</summary>
public static partial class StyleBlocks
{
    /// <summary>
    /// Closes braces a &lt;style&gt; block leaves open. The renderer reads past the end of a stylesheet with an
    /// unclosed block after an at-rule (<c>@media … {</c>, cut off by the sender) and fails with an
    /// IndexOutOfRangeException, so the mail could not be shown at all.
    /// </summary>
    public static string CloseOpenBraces(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (!html.Contains("<style", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        return StyleContent().Replace(html, m =>
        {
            var css = m.Groups["css"].Value;
            var (missing, openComment) = MissingBraces(css);
            if (missing == 0)
            {
                return m.Value;
            }

            // The renderer drops an unclosed comment up to the end – the closing braces must come after it.
            return m.Groups["open"].Value + css + (openComment ? "*/" : string.Empty) + new string('}', missing);
        });
    }

    // How many "}" the end needs so that every "{" – counted from any position – is closed. Like the renderer,
    // braces inside /* comments */ do not count.
    private static (int Missing, bool OpenComment) MissingBraces(string css)
    {
        var code = new StringBuilder(css.Length);
        var i = 0;
        var openComment = false;
        while (i < css.Length)
        {
            var start = css.IndexOf("/*", i, StringComparison.Ordinal);
            if (start < 0)
            {
                code.Append(css, i, css.Length - i);
                break;
            }

            code.Append(css, i, start - i);
            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                openComment = true;
                break;
            }

            i = end + 2;
        }

        // From the end backwards: the largest surplus of "{" over "}" in any tail of the stylesheet.
        var balance = 0;
        var missing = 0;
        for (var j = code.Length - 1; j >= 0; j--)
        {
            balance += code[j] switch { '{' => 1, '}' => -1, _ => 0 };
            missing = Math.Max(missing, balance);
        }

        return (missing, openComment);
    }

    [GeneratedRegex(@"(?<open><style\b[^>]*>)(?<css>.*?)(?=</style\s*>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleContent();
}
