using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// The "paper" mail content is shown on. In the dark theme, plain-text and simply formatted mails are shown dark;
/// designed mails (own backgrounds, e.g. newsletters) keep their light page – darkening them would make them unreadable.
/// </summary>
internal static partial class MailPaper
{
    // A hard Enter (paragraph, line of a text mail) a little more apart than a line that merely wraps or a soft break;
    // the line height itself stays the renderer's (on Windows, more made soft breaks look like hard ones).
    // HTML5's sections are blocks; the renderer only knows the older elements and laid them out like text – the tables
    // inside a newsletter's <header>/<section> vanished (Apple Music's weekly mail showed only its logo).
    private const string Common =
        " header, footer, section, article, nav, aside, main, figure, figcaption, hgroup, address, details, summary { display: block; }" +
        " pre, code { font-family: Consolas, 'Cascadia Mono', monospace; }" +
        " p { margin-top: 0; margin-bottom: 0.3em; } .neruna-text > div { margin-bottom: 0.3em; }";

    // Outlook writes every line as its own paragraph with margin 0 (p.MsoNormal in the mail's own <style>), so a
    // hard Enter looked like a wrapped line; this comes after the mail's styles and wins.
    private const string ParagraphSpacing =
        "<style>p.MsoNormal, li.MsoNormal, div.MsoNormal, p.MsoPlainText { margin-bottom: 0.3em; }</style>";

    public const string LightStylesheet =
        "body { font-family: 'Segoe UI', Inter, Arial, sans-serif; font-size: 10.5pt; color: #1b1b1b; margin: 0; }" +
        " a { color: #0F6CBD; } blockquote { border-left: 2px solid #C8C8C8; margin-left: 4px; padding-left: 10px; color: #424242; }" + Common;

    public const string DarkStylesheet =
        "body { font-family: 'Segoe UI', Inter, Arial, sans-serif; font-size: 10.5pt; color: #E6E6E6; margin: 0; }" +
        " a { color: #6CB8F6; } blockquote { border-left: 2px solid #5A5A5A; margin-left: 4px; padding-left: 10px; color: #BDBDBD; }" + Common;

    public static readonly IBrush DarkBrush = new SolidColorBrush(Color.Parse("#292929"));

    public static bool IsDarkTheme => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>
    /// How to show <paramref name="html"/>: dark (with black text colours taken out, so they follow the page) or on
    /// the light page as the sender designed it.
    /// </summary>
    public static (string Html, bool Dark) Prepare(string html, bool darkTheme)
    {
        ArgumentNullException.ThrowIfNull(html);
        html = Neruna.Core.Mail.StyleBlocks.CloseOpenBraces(html);

        // The renderer knows no media queries and applies their rules always: a phone's "@media (max-width: 480px)
        // { td { width: 100% } }" made five cells side by side five screens wide. The reading pane is no phone.
        html = SmallScreenRules().Replace(html, string.Empty);

        // The renderer knows no flexbox or grid (laid out as something else, a mail grew to many screens of empty
        // space): the elements are rewritten into tables and blocks as the sender laid them out, then the
        // declarations go.
        html = FlexLayout.Apply(html);
        html = FlexOrGrid().Replace(html, string.Empty);

        // An empty paragraph (Outlook: <p><o:p>&nbsp;</o:p></p>) is a blank line; the renderer let it collapse.
        html = EmptyParagraph().Replace(html, "${open}<br>${close}") + ParagraphSpacing;
        if (!darkTheme || OwnBackground().IsMatch(html))
        {
            return (html, false);
        }

        // The light text colour also goes on a wrapper: the renderer does not reliably apply the base stylesheet's
        // body colour (in the editor preview the text stayed black).
        var light = TextColor().Replace(html, m => ForDarkPage(m.Value, m.Groups["value"].Value));
        return ("<div style=\"color: #E6E6E6\">" + light + "</div>", true);
    }

    [GeneratedRegex(@"(?<![-\w])display\s*:\s*(inline-)?(flex|grid)\s*(!\s*important)?\s*;?", RegexOptions.IgnoreCase)]
    private static partial Regex FlexOrGrid();

    // "@media … max-width …" with its rules (one level of braces inside).
    [GeneratedRegex(@"@media[^{;]*max-(device-)?width[^{;]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", RegexOptions.IgnoreCase)]
    private static partial Regex SmallScreenRules();

    [GeneratedRegex(@"(?<open><p\b[^>]*>)(?:\s|&nbsp;|&#160;|\u00a0|</?o:p>|<span\b[^>]*>|</span>)*(?<close></p>)", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyParagraph();

    // Backgrounds a mail sets itself (not "background: transparent / none").
    [GeneratedRegex(@"bgcolor\s*=|background(-color|-image)?\s*:\s*(?!\s*(transparent|none|inherit|initial)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex OwnBackground();

    // Dark text colours would vanish on the dark page: black and grey are dropped (the text follows the page colour),
    // dark colours are lightened, so a blue signature line stays blue.
    private static string ForDarkPage(string declaration, string value)
    {
        if (!TryParse(value, out var r, out var g, out var b) || 0.2126 * r + 0.7152 * g + 0.0722 * b >= 110)
        {
            return declaration;
        }

        if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 40)
        {
            return string.Empty;
        }

        static int Lighten(int c) => c + (int)((255 - c) * 0.55);
        var lighter = $"#{Lighten(r):X2}{Lighten(g):X2}{Lighten(b):X2}";
        return declaration.Contains(':', StringComparison.Ordinal)
            ? $"color: {lighter};"
            : $" color=\"{lighter}\"";
    }

    private static bool TryParse(string value, out int r, out int g, out int b)
    {
        var v = value.Trim().ToLowerInvariant();
        (r, g, b) = (0, 0, 0);
        if (v is "black" or "windowtext")
        {
            return true;
        }

        if (v.StartsWith('#') && (v.Length == 4 || v.Length == 7) && int.TryParse(v[1..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            (r, g, b) = v.Length == 4
                ? (((hex >> 8) & 0xF) * 17, ((hex >> 4) & 0xF) * 17, (hex & 0xF) * 17)
                : ((hex >> 16) & 0xFF, (hex >> 8) & 0xFF, hex & 0xFF);
        }
        else if (Rgb().Match(v) is { Success: true } rgb)
        {
            (r, g, b) = (int.Parse(rgb.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(rgb.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(rgb.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            return false;
        }

        return true;
    }

    // A text colour in an inline style (color: …;) or as <font color="…">.
    [GeneratedRegex(@"(?<![-\w])color\s*:\s*(?<value>#[0-9a-f]{3,6}|rgba?\([^)]*\)|[a-z]+)\s*(!important)?\s*;?|\scolor\s*=\s*[""']?(?<value>#[0-9a-f]{3,6}|[a-z]+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex TextColor();

    [GeneratedRegex(@"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)")]
    private static partial Regex Rgb();
}
