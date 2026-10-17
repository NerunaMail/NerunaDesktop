using System.Net;
using System.Text.RegularExpressions;

namespace Neruna.Core.Mail;

/// <summary>
/// Links to files ("file:///C:/Projekte/Offerte.pdf", "file://server/freigabe/Offerte.pdf"): inserted by dragging a
/// file into a mail with Ctrl. In received mails they are plain text – a click on a file is a click on a program –
/// except from senders set under Einstellungen → Datenschutz (see <see cref="Privacy.PrivacyOptions.OpensFileLinks"/>).
/// </summary>
public static partial class FileLinks
{
    /// <summary>File types that run something when opened: asked once more before opening even from safe senders.</summary>
    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".scr", ".pif", ".lnk", ".url", ".hta", ".cpl", ".jar", ".reg", ".appref-ms", ".application", ".sh", ".desktop",
        ".appimage", ".app", ".command",
    };

    public static bool IsFileLink(string? href) => href?.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The link for a local path or a path on a share.</summary>
    public static string ToUri(string path) => new Uri(path).AbsoluteUri;

    /// <summary>The path a link stands for, as the system writes it (C:\…, \\server\…, /home/…); null if it is none.</summary>
    public static string? ToPath(string? href)
    {
        if (!IsFileLink(href) || !Uri.TryCreate(href!.Trim(), UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return null;
        }

        return uri.IsUnc ? uri.LocalPath : Uri.UnescapeDataString(uri.LocalPath);
    }

    public static bool IsExecutable(string path) => Executables.Contains(Path.GetExtension(path));

    /// <summary>
    /// File links in received HTML as plain text: the link's text, followed by the path in brackets when the text does
    /// not show it already – so it can still be copied.
    /// </summary>
    public static string AsText(string html) => FileAnchor().Replace(html, match =>
    {
        var inner = match.Groups["inner"].Value;
        var path = ToPath(WebUtility.HtmlDecode(match.Groups["href"].Value)) ?? WebUtility.HtmlDecode(match.Groups["href"].Value);
        var text = WebUtility.HtmlDecode(Tags().Replace(inner, string.Empty));
        var shown = text.Contains(path, StringComparison.OrdinalIgnoreCase) ? string.Empty : " (" + WebUtility.HtmlEncode(path) + ")";
        return "<span class=\"neruna-file-link\">" + inner + "</span>" + shown;
    });

    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*(?<q>[""'])(?<href>\s*file:[^""']*)\k<q>[^>]*>(?<inner>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FileAnchor();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
