using System.Net;
using System.Text.RegularExpressions;

namespace Neruna.Core.Mail;

/// <summary>
/// The preview line in the message list: what the server or the plain-text part gives, without technical leftovers –
/// picture references ("[cid:image001.png@…]"), links in angle brackets ("Offerte&lt;https://…&gt;"), HTML tags and
/// entities – and on one line.
/// </summary>
public static partial class MailPreview
{
    public static string Clean(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return string.Empty;
        }

        var text = Leftovers().Replace(preview, " ");
        text = Tags().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        // A link cut off by the end of the preview.
        text = CutLink().Replace(text, string.Empty);
        return Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\[(?:cid|image):[^\]]*\]?|<(?:https?|mailto|ftp|tel):[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Leftovers();

    [GeneratedRegex(@"<(?:[a-zA-Z][a-zA-Z0-9:-]*|/[a-zA-Z][a-zA-Z0-9:-]*|!--)[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"<(?:https?|mailto):\S*$", RegexOptions.IgnoreCase)]
    private static partial Regex CutLink();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
