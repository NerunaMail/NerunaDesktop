using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Text;
using MimeKit.Tnef;

namespace Neruna.Core.Mail;

/// <summary>
/// What the reading pane shows: the sanitized body as HTML, the images it embeds and every real attachment.
/// </summary>
/// <remarks>
/// Attachments are determined structurally, not only by Content-Disposition: many mail programs send
/// PDFs as "inline" or without disposition, some wrap attachments in winmail.dat (TNEF).
/// Remote content (tracking pixels, external CSS images) is blocked unless explicitly allowed.
/// </remarks>
public sealed partial class MessageContent
{
    private static readonly HashSet<string> RemovedWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript", "audio", "video", "canvas", "svg", "math", "template",
    };

    private static readonly HashSet<string> TableTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col",
    };

    private static readonly HashSet<string> RemovedTagOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "form", "input", "button", "select", "option", "textarea", "meta", "link", "base", "source", "track", "param",
    };

    // Keeps the layout of blocked images without showing a broken-image icon (tracking pixels, banners).
    private const string TransparentPixel = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "background", "poster", "srcset", "lowsrc", "dynsrc", "data",
    };

    private MessageContent(string html, string plainText, IReadOnlyDictionary<string, MimePart> inlineParts, IReadOnlyList<MimeEntity> attachments, bool hasBlockedRemoteContent)
    {
        Html = html;
        PlainText = plainText;
        InlineParts = inlineParts;
        Attachments = attachments;
        HasBlockedRemoteContent = hasBlockedRemoteContent;
    }

    /// <summary>Tracking pixels found (and kept out, also when pictures are loaded and they are blocked).</summary>
    public int Trackers { get; private init; }

    /// <summary>Shown as plain text although the mail has a layout of its own (setting "Nur Text"); the UI offers the original.</summary>
    public bool ShowsPlainTextOnly { get; private init; }

    /// <summary>The links of the HTML body with their texts and hover texts, for the check before a link opens.</summary>
    public IReadOnlyList<Privacy.HtmlLink> Links { get; private init; } = [];

    /// <summary>Checks a clicked link with the text and hover text it has in this mail (the most deceptive one if it occurs more than once).</summary>
    public Privacy.LinkCheck CheckLink(string href, bool clean)
    {
        ArgumentNullException.ThrowIfNull(href);
        // The view may hand over the link normalized (Android's WebView does): compared as URLs too.
        static string? Normal(string link) => Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri : null;
        var normal = Normal(href);
        var checks = Links.Where(l => string.Equals(l.Href.Trim(), href.Trim(), StringComparison.Ordinal) || (normal is not null && Normal(l.Href) == normal))
            .Select(l => Privacy.LinkInspector.Inspect(href, l.Text, l.Title, clean)).ToList();
        return checks.MaxBy(c => c.Findings.Count == 0 ? -1 : (int)c.Findings.Max(f => f.Severity)) ?? Privacy.LinkInspector.Inspect(href, clean: clean);
    }

    /// <summary>Sanitized HTML document, safe to render (no scripts, no forms, remote content per settings).</summary>
    public string Html { get; }

    /// <summary>Plain text of the body, for quoting in replies.</summary>
    public string PlainText { get; }

    /// <summary>Images referenced from the HTML via <c>cid:</c>, keyed by Content-Id without angle brackets.</summary>
    public IReadOnlyDictionary<string, MimePart> InlineParts { get; }

    public IReadOnlyList<MimeEntity> Attachments { get; }

    /// <summary>True if remote images or styles were removed; the UI offers to load them.</summary>
    public bool HasBlockedRemoteContent { get; }

    /// <param name="allowRemoteContent">Pictures and styles from the internet are loaded.</param>
    /// <param name="blockTrackers">Tracking pixels stay out even then.</param>
    /// <param name="plainTextOnly">The body as plain text only (no layout, nothing from the internet).</param>
    /// <param name="fileLinks">Links to files stay links (a safe sender); otherwise they are shown as text.</param>
    public static MessageContent From(MimeMessage message, bool allowRemoteContent = false, bool blockTrackers = true, bool plainTextOnly = false, bool fileLinks = false)
    {
        ArgumentNullException.ThrowIfNull(message);

        var walker = new Walker();
        if (message.Body is { } body)
        {
            walker.Visit(body);
        }

        string html;
        var blocked = false;
        var trackers = 0;
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        var plainOnly = plainTextOnly && walker.HtmlBody is not null;

        if (plainOnly)
        {
            var text = walker.TextBody is { } textBody0 ? PlainTextOf(textBody0) : HtmlText.ToPlainText(walker.HtmlBody!.Text ?? string.Empty);
            html = "<html><body>" + PlainTextToHtml(text.Trim()) + "</body></html>";
        }
        else if (walker.HtmlBody is { } htmlPart)
        {
            var source0 = htmlPart.Text ?? string.Empty;
            html = Sanitize(fileLinks ? source0 : FileLinks.AsText(source0), allowRemoteContent, blockTrackers, referencedIds, ref blocked, ref trackers);
            foreach (var extra in walker.ExtraBodies)
            {
                html += TextToHtmlFragment(extra);
            }
        }
        else if (walker.TextBody is { } textPart)
        {
            html = "<html><body>" + TextToHtmlFragment(textPart) + string.Concat(walker.ExtraBodies.Select(TextToHtmlFragment)) + "</body></html>";
        }
        else
        {
            html = "<html><body></body></html>";
        }

        // Parts referenced from the HTML are shown inline; everything else that is not the body is an attachment.
        var inline = new Dictionary<string, MimePart>(StringComparer.Ordinal);
        var attachments = new List<MimeEntity>();
        foreach (var candidate in walker.Candidates)
        {
            if (candidate is MimePart { ContentId: { } id } part && referencedIds.Contains(id))
            {
                inline[id] = part;
            }
            else
            {
                attachments.Add(candidate);
            }
        }

        var plain = walker.TextBody is { } textBody ? PlainTextOf(textBody) : walker.HtmlBody?.Text is { } h ? HtmlText.ToPlainText(h) : string.Empty;
        var links = walker.HtmlBody?.Text is { } source ? Privacy.HtmlScan.Of(source).Links : [];
        return new MessageContent(html, plain.Trim(), inline, attachments, blocked) { Trackers = trackers, ShowsPlainTextOnly = plainOnly, Links = links };
    }

    /// <summary>"cid:part1.abc@x" → "part1.abc@x" (URL-decoded), or null for other URLs.</summary>
    public static string? ContentIdOf(string? src) =>
        src is not null && src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(src[4..]).Trim('<', '>') : null;

    /// <summary>File name shown for an attachment.</summary>
    /// <summary>The attachment's name as shown and saved – without invisible characters that could disguise its type.</summary>
    public static string FileNameOf(MimeEntity entity) => Security.OpenedAttachments.CleanName(entity switch
    {
        MessagePart { Message.Subject: { Length: > 0 } subject } => subject + ".eml",
        MessagePart => "Nachricht.eml",
        _ => entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? DefaultName(entity.ContentType),
    });

    private static string DefaultName(ContentType type) => type.MediaType switch
    {
        "text" when type.MediaSubtype.Equals("calendar", StringComparison.OrdinalIgnoreCase) => "einladung.ics",
        "image" => "bild." + type.MediaSubtype.ToLowerInvariant(),
        _ => "anhang",
    };

    private static string TextToHtmlFragment(TextPart part)
    {
        if (part.IsHtml)
        {
            var ignored = new HashSet<string>(StringComparer.Ordinal);
            var blocked = false;
            var trackers = 0;
            return "<hr/>" + Sanitize(FileLinks.AsText(part.Text ?? string.Empty), false, true, ignored, ref blocked, ref trackers);
        }

        // format=flowed (Thunderbird) is first unwrapped to plain text: MimeKit's FlowedToHtml turns every line into
        // its own <p>, which renders with large gaps. Plain text then takes the same path as every other text mail.
        return PlainTextToHtml(PlainTextOf(part));
    }

    /// <summary>
    /// One block per line, spaces kept with &amp;nbsp;, links clickable. Deliberately without <c>white-space: pre-wrap</c>:
    /// HtmlRenderer handles it inconsistently (on Windows whole mails ended up on one line).
    /// </summary>
    public static string PlainTextToHtml(string text)
    {
        var html = new StringBuilder("<div class=\"neruna-text\">");
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n').Split('\n');
        foreach (var line in lines)
        {
            html.Append("<div>").Append(line.Length == 0 ? "<br>" : LineToHtml(line)).Append("</div>");
        }

        return html.Append("</div>").ToString();
    }

    private static string LineToHtml(string line)
    {
        var result = new StringBuilder();
        var last = 0;
        foreach (Match url in PlainUrl().Matches(line))
        {
            result.Append(KeepSpaces(line[last..url.Index]));
            var href = url.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url.Value : url.Value;
            result.Append("<a href=\"").Append(Encode(href)).Append("\">").Append(Encode(url.Value)).Append("</a>");
            last = url.Index + url.Length;
        }

        return result.Append(KeepSpaces(line[last..])).ToString();
    }

    // Only what HTML needs; umlauts etc. stay readable in the source.
    private static string Encode(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    // Tabs become four spaces; runs of spaces (and leading spaces) stay visible, single spaces can still wrap.
    // HtmlRenderer collapses &nbsp; runs, so the runs go into a "white-space: pre" span (spaces only, no line breaks).
    private static string KeepSpaces(string text) =>
        SpaceRun().Replace(Encode(text.Replace("\t", "    ", StringComparison.Ordinal)), run => "<span style=\"white-space: pre\">" + run.Value + "</span>");

    /// <summary>The text of a plain part, with format=flowed soft line breaks joined (RFC 3676).</summary>
    private static string PlainTextOf(TextPart part)
    {
        var text = part.Text ?? string.Empty;
        if (!part.IsFlowed)
        {
            return text;
        }

        var deleteSpace = part.ContentType.Parameters.TryGetValue("delsp", out string? delsp) && string.Equals(delsp, "yes", StringComparison.OrdinalIgnoreCase);
        text = SignatureSeparator().Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), "--");
        return new FlowedToText { DeleteSpace = deleteSpace }.Convert(text);
    }

    private static bool IsValidAttributeName(string name) =>
        name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    private static string Sanitize(string html, bool allowRemote, bool blockTrackers, HashSet<string> referencedIds, ref bool blocked, ref int trackers)
    {
        var blockedLocal = false;
        var trackersLocal = 0;
        // Our tag callback is the sanitizer (MimeKit's FilterHtml is deprecated); the renderer runs no scripts anyway.
        var converter = new HtmlToHtml
        {
            FilterComments = true,
            HtmlTagCallback = (context, writer) =>
            {
                var name = context.TagName;
                if (RemovedWithContent.Contains(name))
                {
                    context.DeleteTag = true;
                    context.DeleteEndTag = true;
                    context.SuppressInnerContent = true;
                    return;
                }

                if (RemovedTagOnly.Contains(name))
                {
                    context.DeleteTag = true;
                    context.DeleteEndTag = true;
                    return;
                }

                if (context.IsEndTag)
                {
                    context.WriteTag(writer, true);
                    return;
                }

                context.WriteTag(writer, false);
                var tracker = name.Equals("img", StringComparison.OrdinalIgnoreCase) && IsTrackingPixel(context.Attributes);
                if (tracker)
                {
                    trackersLocal++;
                }

                var centered = false;
                var styled = false;
                var placeholder = false;
                var picture = false;

                // A blocked picture without a height of its own keeps no width either: the desktop renderer makes the
                // transparent pixel a square of that width ("100%": screen-high empty areas), whatever height it gets.
                var hasHeight = context.Attributes.Any(a => a.Name.Equals("height", StringComparison.OrdinalIgnoreCase));
                var dropWidth = !hasHeight && name.Equals("img", StringComparison.OrdinalIgnoreCase)
                                && context.Attributes.FirstOrDefault(a => a.Name.Equals("src", StringComparison.OrdinalIgnoreCase))?.Value is { } source
                                && IsRemote(source) && (!allowRemote || (tracker && blockTrackers));
                foreach (var attribute in context.Attributes.OrderBy(a => a.Name.Equals("style", StringComparison.OrdinalIgnoreCase)))
                {
                    var value = attribute.Value ?? string.Empty;
                    var attributeName = attribute.Name;

                    // Broken HTML (style=font-family:'Segoe ui',sans-serif; without quotes) yields names like
                    // "ui',sans-serif;" – they mean nothing and cannot be written; dropped instead of failing the message.
                    if (!IsValidAttributeName(attributeName) || attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || IsScriptUrl(value))
                    {
                        continue;
                    }

                    if (UrlAttributes.Contains(attributeName))
                    {
                        if (ContentIdOf(value) is { } cid)
                        {
                            referencedIds.Add(cid);
                        }
                        else if (IsRemote(value) && (!allowRemote || (tracker && blockTrackers)))
                        {
                            // A tracker kept out although pictures are allowed is no reason to offer "Bilder laden" again.
                            blockedLocal |= !allowRemote;
                            if (attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
                            {
                                writer.WriteAttribute("src", TransparentPixel);
                                placeholder = picture = name.Equals("img", StringComparison.OrdinalIgnoreCase);
                            }

                            continue;
                        }
                    }

                    if (dropWidth && attributeName.Equals("width", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isTable = name.Equals("table", StringComparison.OrdinalIgnoreCase);
                    if (isTable && attributeName.Equals("align", StringComparison.OrdinalIgnoreCase) && value.Trim().Equals("center", StringComparison.OrdinalIgnoreCase))
                    {
                        // The renderer centers a table wider than its space to the left, out of view; auto margins
                        // center it as well and keep it inside (see TableStyle).
                        centered = true;
                        continue;
                    }

                    if (attributeName.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        value = SanitizeCss(value, allowRemote, referencedIds, ref blockedLocal);
                        if (placeholder)
                        {
                            value = PlaceholderStyle(value, context.Attributes);
                            placeholder = false;
                        }

                        if (isTable)
                        {
                            value = TableStyle(value, centered);
                            styled = true;
                        }

                        // Newsletter builders lay out <div>s as table and cells (display: table / table-cell); the
                        // renderer then shows nothing at all. As blocks the content is there (columns one below the other).
                        if (!TableTags.Contains(name))
                        {
                            value = DisplayTable().Replace(value, "display: block");
                        }
                    }

                    writer.WriteAttribute(attributeName, value);
                }

                // A blocked picture without a style attribute of its own (the style attribute comes last, see OrderBy).
                if (placeholder)
                {
                    writer.WriteAttribute("style", PlaceholderStyle(string.Empty, context.Attributes));
                }

                // The desktop renderer sizes a picture by its attributes only (not its style): without a height it keeps
                // the pixel's square shape at the given width.
                if (picture && !hasHeight)
                {
                    writer.WriteAttribute("height", "1");
                }

                if (centered && !styled)
                {
                    writer.WriteAttribute("style", TableStyle(string.Empty, centered: true));
                }

                // Links open in the browser, never inside the mail view.
                if (name.Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteAttribute("target", "_blank");
                }
            },
        };

        var result = CarryRowBackgrounds(LimitColspans(DropStrayTableContent(ExpandPreformatted(converter.Convert(html)))));

        // CSS in <style> blocks is passed through; remote URLs there are blocked when rendering, but report them.
        if (!allowRemote && RemoteCssUrl().IsMatch(result))
        {
            blockedLocal = true;
        }

        foreach (Match match in CssCid().Matches(result))
        {
            referencedIds.Add(Uri.UnescapeDataString(match.Groups[1].Value));
        }

        blocked |= blockedLocal;
        trackers += trackersLocal;
        return result;
    }

    /// <summary>
    /// The transparent pixel standing in for a blocked picture keeps the picture's height only when the mail gives one;
    /// with just a width ("100%") it would be stretched to a square that size – screen-high empty areas in newsletters.
    /// An inline height also wins over a view's own "img { height: auto }".
    /// </summary>
    private static string PlaceholderStyle(string style, HtmlAttributeCollection attributes)
    {
        // A height in pixels of its own stays (it is the picture's place in the layout).
        if (FixedHeight().IsMatch(style))
        {
            return style;
        }

        var height = attributes.FirstOrDefault(a => a.Name.Equals("height", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        var pixels = int.TryParse(height?.Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
        var rule = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"height: {pixels}px");
        return string.IsNullOrWhiteSpace(style) ? rule : style.TrimEnd().TrimEnd(';') + "; " + rule;
    }

    /// <summary>
    /// A picture that only tells the sender the mail was opened: (almost) invisible – at most 25 pixels in area, or
    /// limited to 1 pixel by its style – or from a known counting address (".../track/open", "/wf/open" …).
    /// </summary>
    private static bool IsTrackingPixel(HtmlAttributeCollection attributes)
    {
        string? Get(string attribute) => attributes.FirstOrDefault(a => a.Name.Equals(attribute, StringComparison.OrdinalIgnoreCase))?.Value;
        var src = Get("src");
        if (src is null || !IsRemote(src))
        {
            return false;
        }

        if (TrackingPath().IsMatch(src))
        {
            return true;
        }

        if (Get("style") is { } style && TinyStyle().IsMatch(style))
        {
            return true;
        }

        static int? Pixels(string? value) => int.TryParse(value?.Trim().Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        var width = Pixels(Get("width"));
        var height = Pixels(Get("height"));
        if (width is null || height is null)
        {
            return width is 0 || height is 0;
        }

        return width.Value * height.Value <= 25;
    }

    [GeneratedRegex(@"(?<![-\w])height\s*:\s*\d+(\.\d+)?px", RegexOptions.IgnoreCase)]
    private static partial Regex FixedHeight();

    [GeneratedRegex(@"/(track/open|wf/open|e/o/|open\.php|open\.aspx|trk/open|t/open|o\.gif|pixel\.gif|beacon|tracking/open)|[?&](open|pixel)=|google-analytics\.com/|/__utm\.gif", RegexOptions.IgnoreCase)]
    private static partial Regex TrackingPath();

    [GeneratedRegex(@"(?<![-\w])(max-)?(width|height)\s*:\s*[01](px)?\s*(;|$|!)|display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.IgnoreCase)]
    private static partial Regex TinyStyle();

    /// <summary>
    /// Tables as the renderer can show them: a max-width cuts off content wider than it (browsers widen the table
    /// instead), so it goes; "margin: auto" (which the renderer reads only as margin-left/-right) and align="center"
    /// become auto side margins.
    /// </summary>
    private static string TableStyle(string css, bool centered)
    {
        css = TableMaxWidth().Replace(css, string.Empty);
        if (centered || MarginAuto().IsMatch(css))
        {
            css = MarginAuto().Replace(css, string.Empty).TrimEnd().TrimEnd(';');
            css = (css.Length > 0 ? css + "; " : string.Empty) + "margin-left: auto; margin-right: auto";
        }

        return css;
    }

    /// <summary>
    /// Elements written between the cells of a table (Outlook: empty &lt;span style="mso-bookmark:…"&gt; between the
    /// &lt;td&gt;s of a row) – browsers move them out of the table, the renderer took them for cells and shifted the
    /// whole table. They are dropped, as is text there (browsers would show it above the table).
    /// </summary>
    internal static string DropStrayTableContent(string html)
    {
        // Per open table: whether we are inside one of its cells right now.
        var tables = new Stack<bool>();
        var result = new StringBuilder(html.Length);
        foreach (Match token in TagOrText().Matches(html))
        {
            var value = token.Value;
            if (!value.StartsWith('<'))
            {
                if (tables.Count == 0 || tables.Peek() || string.IsNullOrWhiteSpace(value))
                {
                    result.Append(value);
                }

                continue;
            }

            var tag = TagName().Match(value);
            var name = tag.Success ? tag.Groups["name"].Value.ToLowerInvariant() : string.Empty;
            var isEnd = tag.Success && tag.Groups["end"].Success;
            switch (name)
            {
                case "table" when !isEnd:
                    tables.Push(false);
                    break;
                case "table":
                    tables.TryPop(out _);
                    break;
                case "td" or "th" or "caption" when tables.Count > 0:
                    tables.Pop();
                    tables.Push(!isEnd);
                    break;
                case "tr" or "tbody" or "thead" or "tfoot" or "colgroup" or "col" when tables.Count > 0:
                    tables.Pop();
                    tables.Push(false);
                    break;
                default:
                    if (tables.Count > 0 && !tables.Peek() && !value.StartsWith("<!", StringComparison.Ordinal))
                    {
                        continue; // between cells: dropped
                    }

                    break;
            }

            result.Append(value);
        }

        return result.ToString();
    }

    /// <summary>
    /// The renderer paints no background on table rows (ticket systems: white heading on a colored &lt;tr&gt;, then
    /// invisible); the cells of such a row get the color themselves, unless they have their own.
    /// </summary>
    internal static string CarryRowBackgrounds(string html)
    {
        // Tables (IsRow false) and their open row; a <tr> without </tr> ends at the next <tr> or at </table>.
        var open = new Stack<(bool IsRow, string? Color)>();
        return TableStructureTag().Replace(html, tag =>
        {
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var isEnd = tag.Groups["end"].Success;
            if (name is "table" or "tr")
            {
                if (open.TryPeek(out var top) && top.IsRow && (name == "tr" || isEnd))
                {
                    open.Pop();
                }

                if (name == "table" && isEnd)
                {
                    open.TryPop(out _);
                }
                else if (!isEnd)
                {
                    open.Push((name == "tr", name == "tr" ? RowColor().Match(tag.Value) is { Success: true } match ? match.Groups["color"].Value : null : null));
                }

                return tag.Value;
            }

            if (isEnd || !open.TryPeek(out var row) || !row.IsRow || row.Color is not { } color || CellBackground().IsMatch(tag.Value))
            {
                return tag.Value;
            }

            return StyleAttribute().Match(tag.Value) is { Success: true } style
                ? tag.Value.Insert(style.Groups[1].Index, "background-color: " + color + "; ")
                : tag.Value.Insert(tag.Value.Length - (tag.Value.EndsWith("/>", StringComparison.Ordinal) ? 2 : 1), " style=\"background-color: " + color + "\"");
        });
    }

    /// <summary>
    /// Cells spanning more columns than their table has (newsletters: colspan="3" in a one-column table) make the renderer
    /// show nothing at all. Browsers ignore the surplus; here the colspan is cut down to the columns the table really has.
    /// </summary>
    internal static string LimitColspans(string html)
    {
        var tables = new Stack<(int[] Columns, List<int>? Row)>();
        var cells = new List<(int[] Columns, List<int> Row, int Index)>();
        foreach (Match tag in TableStructureTag().Matches(html))
        {
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var isEnd = tag.Groups["end"].Success;
            if (name == "table")
            {
                if (!isEnd)
                {
                    tables.Push(([0], null));
                }
                else if (tables.Count > 0)
                {
                    EndRow(tables.Pop());
                }
            }
            else if (tables.Count > 0 && !isEnd)
            {
                var table = tables.Pop();
                if (name == "tr")
                {
                    EndRow(table);
                    table = (table.Columns, []);
                }
                else
                {
                    table.Row ??= [];
                    cells.Add((table.Columns, table.Row, tag.Index));
                    table.Row.Add(tag.Index);
                }

                tables.Push(table);
            }
        }

        while (tables.Count > 0)
        {
            EndRow(tables.Pop());
        }

        // Each cell may span the columns the other cells of its row leave free.
        var limits = cells.ToDictionary(c => c.Index, c => Math.Max(1, c.Columns[0] - (c.Row.Count - 1)));
        return TableStructureTag().Replace(html, tag =>
            limits.TryGetValue(tag.Index, out var limit) && Colspan().Match(tag.Value) is { Success: true } span
                && int.TryParse(span.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > limit
                ? tag.Value[..span.Groups[1].Index] + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) + tag.Value[(span.Groups[1].Index + span.Groups[1].Length)..]
                : tag.Value);

        static void EndRow((int[] Columns, List<int>? Row) table)
        {
            if (table.Row is { Count: > 0 } row)
            {
                table.Columns[0] = Math.Max(table.Columns[0], row.Count);
            }
        }
    }

    /// <summary>
    /// &lt;pre&gt; blocks (Thunderbird puts plain-text signatures and quotes in &lt;pre class="moz-signature"&gt;) get explicit
    /// line breaks: HtmlRenderer's pre handling is unreliable, on Windows the lines ran together.
    /// </summary>
    private static string ExpandPreformatted(string html) =>
        PreBlock().Replace(html, pre =>
        {
            // HTML ignores one newline directly after <pre>.
            var inner = pre.Groups[2].Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            inner = (inner.StartsWith('\n') ? inner[1..] : inner).TrimEnd('\n');
            var lines = TagOrText().Replace(inner, part => part.Value.StartsWith('<')
                ? part.Value
                : SpaceRun().Replace(part.Value.Replace("\t", "    ", StringComparison.Ordinal), run => "<span style=\"white-space: pre\">" + run.Value + "</span>").Replace("\n", "<br>", StringComparison.Ordinal));
            return "<div" + pre.Groups[1].Value + " style=\"font-family: Consolas, 'Cascadia Mono', 'DejaVu Sans Mono', monospace\">" + lines + "</div>";
        });

    private static string SanitizeCss(string css, bool allowRemote, HashSet<string> referencedIds, ref bool blocked)
    {
        if (css.Contains("expression(", StringComparison.OrdinalIgnoreCase) || css.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        foreach (Match match in CssCid().Matches(css))
        {
            referencedIds.Add(Uri.UnescapeDataString(match.Groups[1].Value));
        }

        if (!allowRemote && RemoteCssUrl().IsMatch(css))
        {
            blocked = true;
            css = RemoteCssUrl().Replace(css, "none");
        }

        // The renderer ignores inches, centimetres and millimetres – Outlook sizes pictures in signatures that way
        // ("width:.4687in"), which showed them at full size. In pixels (96 per inch) they keep their size.
        css = AbsoluteLength().Replace(css, m =>
        {
            var value = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var pixels = value * m.Groups[2].Value.ToLowerInvariant() switch
            {
                "in" => 96,
                "cm" => 96 / 2.54,
                "mm" => 96 / 25.4,
                _ => 16, // pc
            };
            return Math.Round(pixels, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
        });

        // Points work for fonts, but not for sizes ("width:34pt" also showed a picture at full size).
        css = PointSize().Replace(css, m => m.Groups[1].Value
            + Math.Round(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 96 / 72, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px");

        // The renderer understands background-color but not a color in the "background" shorthand.
        return BackgroundShorthandColor().Replace(css, m => m.Value.TrimEnd(';') + "; background-color: " + m.Groups[1].Value + ";");
    }

    // Also a remote address broken over lines in the mail's source ("https\n ://…") counts – kept as it is, it would
    // be a broken picture of full size instead of the small placeholder.
    private static bool IsRemote(string url)
    {
        url = string.Concat(url.Where(c => !char.IsWhiteSpace(c)));
        return url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool IsScriptUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }

    // Trailing punctuation belongs to the sentence, not the link ("siehe https://example.com.").
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]*[^\s<>"".,;:!?)\]']", RegexOptions.IgnoreCase)]
    private static partial Regex PlainUrl();

    [GeneratedRegex(@"<pre\b((?:\s+(?:class|id|dir|lang)=""[^""]*"")*)[^>]*>(.*?)</pre>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PreBlock();

    [GeneratedRegex(@"(?:background(?:-color)?\s*:\s*|bgcolor\s*=\s*[""']?)(?<color>#[0-9a-f]{3,8}\b|rgba?\([^)]*\)|(?!none\b|transparent\b|inherit\b|initial\b|url\b)[a-z]+\b)", RegexOptions.IgnoreCase)]
    private static partial Regex RowColor();

    [GeneratedRegex(@"background(?:-color)?\s*:|bgcolor\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex CellBackground();

    [GeneratedRegex(@"\bstyle\s*=\s*""()", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex(@"(?<![\w-])max-width\s*:[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex TableMaxWidth();

    [GeneratedRegex(@"(?<![\w-])margin\s*:\s*(?:0(?:px)?\s+)?auto(?:\s+(?:0(?:px)?\s+)?auto)?\s*(?:!important)?\s*;?", RegexOptions.IgnoreCase)]
    private static partial Regex MarginAuto();

    [GeneratedRegex(@"<(?<end>/)?(?<name>table|tr|td|th)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TableStructureTag();

    [GeneratedRegex(@"\bcolspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Colspan();

    [GeneratedRegex(@"^<(?<end>/)?(?<name>[a-zA-Z][a-zA-Z0-9:-]*)")]
    private static partial Regex TagName();

    [GeneratedRegex(@"<[^>]*>|[^<]+")]
    private static partial Regex TagOrText();

    [GeneratedRegex(@"(?<=^|\n) +| {2,}")]
    private static partial Regex SpaceRun();

    // RFC 3676 4.3: the signature separator "-- " is never a soft line break.
    [GeneratedRegex(@"^-- $", RegexOptions.Multiline)]
    private static partial Regex SignatureSeparator();

    [GeneratedRegex(@"display\s*:\s*(?:inline-)?table(?:-[a-z-]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayTable();

    [GeneratedRegex(@"(?<![\w.#-])(\d*\.?\d+)(in|cm|mm|pc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteLength();

    [GeneratedRegex(@"((?<![\w-])(?:min-|max-)?(?:width|height)\s*:\s*)(\d*\.?\d+)pt\b", RegexOptions.IgnoreCase)]
    private static partial Regex PointSize();

    [GeneratedRegex(@"url\(\s*['""]?\s*(https?:|//)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteCssUrl();

    [GeneratedRegex(@"url\(\s*['""]?\s*cid:([^'"")\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CssCid();

    [GeneratedRegex(@"(?<![-\w])background\s*:\s*(#[0-9a-f]{3,8}|rgb\([^)]*\)|[a-z]+)\s*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundShorthandColor();

    /// <summary>Walks the MIME tree once and decides what is body, what may be inline and what is an attachment.</summary>
    private sealed class Walker
    {
        public TextPart? HtmlBody { get; private set; }

        public TextPart? TextBody { get; private set; }

        /// <summary>Further inline text parts (Apple Mail splits text around inline images).</summary>
        public List<TextPart> ExtraBodies { get; } = [];

        /// <summary>Non-body parts; those referenced via cid: become inline images, the rest attachments.</summary>
        public List<MimeEntity> Candidates { get; } = [];

        private bool HasBody => HtmlBody is not null || TextBody is not null;

        public void Visit(MimeEntity entity)
        {
            switch (entity)
            {
                case MultipartSigned signed:
                    // Unverified here (see SecureMimeService); show the signed content, skip the signature part.
                    if (signed.Count > 0)
                    {
                        Visit(signed[0]);
                    }

                    break;

                case MultipartAlternative alternative:
                    VisitAlternative(alternative);
                    break;

                case Multipart multipart:
                    foreach (var child in multipart)
                    {
                        Visit(child);
                    }

                    break;

                case TnefPart tnef:
                    VisitTnef(tnef);
                    break;

                case ApplicationPkcs7Signature or ApplicationPkcs7Mime:
                    // Signature blobs or still-encrypted content are not attachments for the user.
                    break;

                case TextPart text when !text.IsAttachment && IsBodyText(text):
                    if (!HasBody)
                    {
                        if (text.IsHtml)
                        {
                            HtmlBody = text;
                        }
                        else
                        {
                            TextBody = text;
                        }
                    }
                    else
                    {
                        ExtraBodies.Add(text);
                    }

                    break;

                default:
                    Candidates.Add(entity);
                    break;
            }
        }

        private void VisitAlternative(MultipartAlternative alternative)
        {
            // The richest renderable alternative wins: HTML (possibly inside multipart/related) over plain text.
            MimeEntity? best = null;
            foreach (var child in alternative)
            {
                if (child is TextPart { IsHtml: true } || (child is MultipartRelated related && ContainsHtml(related)) || (child is TextPart t && IsBodyText(t) && best is null))
                {
                    best = child;
                }
            }

            // The chosen alternative first, so it becomes the body; the others only add a plain text for quoting.
            if (best is not null)
            {
                Visit(best);
            }

            foreach (var child in alternative)
            {
                if (child == best)
                {
                    continue;
                }

                if (child is TextPart t && IsBodyText(t))
                {
                    if (!t.IsHtml && TextBody is null && best is not TextPart { IsHtml: false })
                    {
                        // Keep the plain alternative for quoting.
                        TextBody = t;
                    }
                }
                else if (child is not Multipart)
                {
                    // e.g. text/calendar invitations offered as an alternative.
                    Candidates.Add(child);
                }
            }
        }

        private void VisitTnef(TnefPart tnef)
        {
            MimeMessage converted;
            try
            {
                converted = tnef.ConvertToMessage();
            }
            catch (Exception ex) when (ex is FormatException or IOException or ArgumentException or NotSupportedException)
            {
                Candidates.Add(tnef);
                return;
            }

            if (converted.Body is { } body)
            {
                var hadBody = HasBody;
                var extraBefore = ExtraBodies.Count;
                Visit(body);
                if (hadBody)
                {
                    // The outer message already had a body; TNEF then only contributes attachments.
                    ExtraBodies.RemoveRange(extraBefore, ExtraBodies.Count - extraBefore);
                }
            }
        }

        private static bool ContainsHtml(MultipartRelated related) =>
            related.Root is TextPart { IsHtml: true } || related.OfType<TextPart>().Any(t => t.IsHtml);

        private static bool IsBodyText(TextPart text) =>
            (text.IsHtml || text.IsPlain || text.IsFlowed) && text.ContentDisposition?.FileName is null && text.ContentType.Name is null;
    }
}
