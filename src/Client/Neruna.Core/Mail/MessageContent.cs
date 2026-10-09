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

    /// <summary>Sanitized HTML document, safe to render (no scripts, no forms, remote content per settings).</summary>
    public string Html { get; }

    /// <summary>Plain text of the body, for quoting in replies.</summary>
    public string PlainText { get; }

    /// <summary>Images referenced from the HTML via <c>cid:</c>, keyed by Content-Id without angle brackets.</summary>
    public IReadOnlyDictionary<string, MimePart> InlineParts { get; }

    public IReadOnlyList<MimeEntity> Attachments { get; }

    /// <summary>True if remote images or styles were removed; the UI offers to load them.</summary>
    public bool HasBlockedRemoteContent { get; }

    public static MessageContent From(MimeMessage message, bool allowRemoteContent = false)
    {
        ArgumentNullException.ThrowIfNull(message);

        var walker = new Walker();
        if (message.Body is { } body)
        {
            walker.Visit(body);
        }

        string html;
        var blocked = false;
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);

        if (walker.HtmlBody is { } htmlPart)
        {
            html = Sanitize(htmlPart.Text ?? string.Empty, allowRemoteContent, referencedIds, ref blocked);
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
        return new MessageContent(html, plain.Trim(), inline, attachments, blocked);
    }

    /// <summary>"cid:part1.abc@x" → "part1.abc@x" (URL-decoded), or null for other URLs.</summary>
    public static string? ContentIdOf(string? src) =>
        src is not null && src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(src[4..]).Trim('<', '>') : null;

    /// <summary>File name shown for an attachment.</summary>
    public static string FileNameOf(MimeEntity entity) => entity switch
    {
        MessagePart { Message.Subject: { Length: > 0 } subject } => subject + ".eml",
        MessagePart => "Nachricht.eml",
        _ => entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? DefaultName(entity.ContentType),
    };

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
            return "<hr/>" + Sanitize(part.Text ?? string.Empty, false, ignored, ref blocked);
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

    private static string Sanitize(string html, bool allowRemote, HashSet<string> referencedIds, ref bool blocked)
    {
        var blockedLocal = false;
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
                foreach (var attribute in context.Attributes)
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
                        else if (IsRemote(value) && !allowRemote)
                        {
                            blockedLocal = true;
                            if (attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
                            {
                                writer.WriteAttribute("src", TransparentPixel);
                            }

                            continue;
                        }
                    }

                    if (attributeName.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        value = SanitizeCss(value, allowRemote, referencedIds, ref blockedLocal);
                    }

                    writer.WriteAttribute(attributeName, value);
                }

                // Links open in the browser, never inside the mail view.
                if (name.Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteAttribute("target", "_blank");
                }
            },
        };

        var result = ExpandPreformatted(converter.Convert(html));

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
        return result;
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

        // The renderer understands background-color but not a color in the "background" shorthand.
        return BackgroundShorthandColor().Replace(css, m => m.Value.TrimEnd(';') + "; background-color: " + m.Groups[1].Value + ";");
    }

    private static bool IsRemote(string url) =>
        url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("//", StringComparison.Ordinal);

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

    [GeneratedRegex(@"<[^>]*>|[^<]+")]
    private static partial Regex TagOrText();

    [GeneratedRegex(@"(?<=^|\n) +| {2,}")]
    private static partial Regex SpaceRun();

    // RFC 3676 4.3: the signature separator "-- " is never a soft line break.
    [GeneratedRegex(@"^-- $", RegexOptions.Multiline)]
    private static partial Regex SignatureSeparator();

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
