using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace Neruna.Core.Mail;

/// <summary>What the compose form is prefilled with.</summary>
/// <param name="Body">Plain-text body (used when no HTML editor is available, and as the text alternative).</param>
/// <param name="Attachments">Attachments carried over (forward).</param>
/// <param name="HtmlBody">Formatted body; images may be inline <c>data:</c> URIs and become embedded attachments on send.</param>
/// <param name="DraftRemoteId">Where this draft is saved in "Entwürfe" (replaced on the next save, deleted after sending).</param>
/// <param name="MessageId">Kept across draft saves and for sending, so the saved copy can be found again.</param>
public sealed record ComposeDraft(
    string To,
    string Cc,
    string Subject,
    string Body,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyList<MimeEntity> Attachments,
    string? HtmlBody = null,
    string? DraftRemoteId = null,
    string? MessageId = null,
    string? From = null)
{
    public static ComposeDraft Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, null, [], []);
}

/// <summary>Builds replies and forwards the way business users expect (AW:/WG:, header block above the original).</summary>
public static partial class MessageComposer
{
    // Where servers note the address a message was delivered to (also for Bcc and mailing lists).
    private static readonly string[] DeliveryHeaders = ["Delivered-To", "X-Original-To", "Envelope-To"];

    public static ComposeDraft Reply(MimeMessage original, string ownAddress, bool replyAll) => Reply(original, [ownAddress], replyAll);

    /// <param name="ownAddresses">The account's address and aliases: never replied to; the one the message was sent to
    /// becomes the sender of the reply (<see cref="ComposeDraft.From"/>).</param>
    public static ComposeDraft Reply(MimeMessage original, IReadOnlyCollection<string> ownAddresses, bool replyAll)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(ownAddresses);

        var replyTo = original.ReplyTo.Mailboxes.Any() ? original.ReplyTo.Mailboxes : original.From.Mailboxes;
        var to = Distinct(replyTo, ownAddresses, []);
        var cc = replyAll
            ? Distinct(original.To.Mailboxes.Concat(original.Cc.Mailboxes), ownAddresses, to.Select(m => m.Address))
            : [];

        // To which of the own addresses it was sent: in To/Cc, else what the server noted on delivery (Bcc, lists).
        var delivered = DeliveryHeaders.SelectMany(h => original.Headers.Where(x => x.Field.Equals(h, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value.Trim().Trim('<', '>')));
        var from = original.To.Mailboxes.Concat(original.Cc.Mailboxes).Select(m => m.Address).Concat(delivered)
            .FirstOrDefault(a => ownAddresses.Contains(a, StringComparer.OrdinalIgnoreCase));
        from = ownAddresses.FirstOrDefault(a => string.Equals(a, from, StringComparison.OrdinalIgnoreCase));

        var references = original.References.ToList();
        if (original.MessageId is { } id)
        {
            references.Add(id);
        }

        return new ComposeDraft(
            Format(to),
            Format(cc),
            "AW: " + StripPrefixes(original.Subject),
            QuoteBlock(original),
            original.MessageId,
            references,
            [],
            HtmlQuoteBlock(original),
            From: from);
    }

    public static ComposeDraft Forward(MimeMessage original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return new ComposeDraft(
            string.Empty,
            string.Empty,
            "WG: " + StripPrefixes(original.Subject),
            QuoteBlock(original),
            null,
            [],
            MessageContent.From(original).Attachments.ToList(),
            HtmlQuoteBlock(original));
    }

    /// <param name="forDraft">Saving a draft: incomplete or invalid recipients are tolerated (kept as far as readable).</param>
    public static MimeMessage Build(MailboxAddress from, ComposeDraft draft, IEnumerable<MimeEntity> extraAttachments, bool forDraft = false)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(draft);

        var message = new MimeMessage { Subject = draft.Subject };
        if (draft.MessageId is { } messageId)
        {
            message.MessageId = messageId;
        }

        message.From.Add(from);
        AddRecipients(message.To, draft.To, forDraft);
        AddRecipients(message.Cc, draft.Cc, forDraft);

        if (draft.InReplyTo is { } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
        }

        foreach (var reference in draft.References)
        {
            message.References.Add(reference);
        }

        var body = new BodyBuilder();
        if (draft.HtmlBody is { } html)
        {
            body.HtmlBody = EmbedDataImages(html, body);
            body.TextBody = HtmlText.ToPlainText(html);
        }
        else
        {
            body.TextBody = draft.Body;
        }

        foreach (var attachment in draft.Attachments.Concat(extraAttachments))
        {
            body.Attachments.Add(attachment);
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    /// <summary>Removes any stack of reply/forward prefixes in German and English ("AW: Re: WG: …").</summary>
    public static string StripPrefixes(string? subject)
    {
        var result = subject ?? string.Empty;
        while (PrefixPattern().Match(result) is { Success: true } match)
        {
            result = result[match.Length..];
        }

        return result.Trim();
    }

    public static string PlainTextOf(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            return message.TextBody;
        }

        return message.HtmlBody is { } html ? HtmlText.ToPlainText(html) : string.Empty;
    }

    /// <summary>Opens a saved draft for editing again: recipients, text with its images, attachments.</summary>
    public static ComposeDraft FromDraft(MimeMessage draft, string remoteId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var content = MessageContent.From(draft);
        var hasHtml = draft.HtmlBody is not null;
        return new ComposeDraft(
            draft.To.ToString(),
            draft.Cc.ToString(),
            draft.Subject ?? string.Empty,
            hasHtml ? string.Empty : draft.TextBody ?? string.Empty,
            draft.InReplyTo,
            draft.References.ToList(),
            content.Attachments,
            hasHtml ? InlineImagesAsDataUris(content) : null,
            remoteId,
            draft.MessageId,
            draft.From.Mailboxes.FirstOrDefault()?.Address);
    }

    private static void AddRecipients(InternetAddressList list, string text, bool lenient)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!lenient)
        {
            list.AddRange(InternetAddressList.Parse(text));
            return;
        }

        // A draft may hold half-typed addresses: keep every part that parses on its own.
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (InternetAddressList.TryParse(part, out var parsed))
            {
                list.AddRange(parsed);
            }
        }
    }

    // Embedded images become data: URIs, so the editor can show them (they become cid: attachments again on send).
    private static string InlineImagesAsDataUris(MessageContent content)
    {
        var html = content.Html;
        foreach (var (id, part) in content.InlineParts)
        {
            if (part.Content is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);
            html = html.Replace("cid:" + id, $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.ToArray())}", StringComparison.OrdinalIgnoreCase);
        }

        return html;
    }

    /// <summary>
    /// Reply header (Von/Gesendet/An/Betreff) above the original, which keeps its formatting; embedded images of the original
    /// are inlined as data URIs so the editor can show them (they become cid: attachments again on send).
    /// </summary>
    private static string HtmlQuoteBlock(MimeMessage original)
    {
        var culture = Localization.Texts.Culture;
        var html = InlineImagesAsDataUris(MessageContent.From(original));

        var header = new StringBuilder()
            .Append("<div><br></div><div><br></div>")
            .Append("<div style=\"border:none;border-top:solid #E1E1E1 1pt;padding:3pt 0 0 0;font-family:Calibri,Arial,sans-serif;font-size:11pt\">")
            .Append(CultureInfo.InvariantCulture, $"<b>Von:</b> {Encode(Format(original.From.Mailboxes))}<br>")
            .Append(CultureInfo.InvariantCulture, $"<b>Gesendet:</b> {Encode(original.Date.LocalDateTime.ToString("dddd, d. MMMM yyyy HH:mm", culture))}<br>")
            .Append(CultureInfo.InvariantCulture, $"<b>An:</b> {Encode(Format(original.To.Mailboxes))}<br>");
        if (original.Cc.Mailboxes.Any())
        {
            header.Append(CultureInfo.InvariantCulture, $"<b>Cc:</b> {Encode(Format(original.Cc.Mailboxes))}<br>");
        }

        header.Append(CultureInfo.InvariantCulture, $"<b>Betreff:</b> {Encode(original.Subject ?? string.Empty)}</div><div><br></div>");
        // A quoted Neruna mail must not carry our signature marker, or choosing a signature would replace the quoted one.
        var quoted = BodyOf(html).Replace($"id=\"{SignatureBlock.ElementId}\"", string.Empty, StringComparison.Ordinal);
        return header + "<div>" + StylesOf(html) + quoted + "</div>";
    }

    /// <summary>Turns inline data: images (pasted or quoted) into embedded attachments referenced by cid:.</summary>
    private static string EmbedDataImages(string html, BodyBuilder body) =>
        DataImage().Replace(html, match =>
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(match.Groups[2].Value);
            }
            catch (FormatException)
            {
                return match.Value;
            }

            var type = ContentType.Parse(match.Groups[1].Value);
            var image = body.LinkedResources.Add("bild." + type.MediaSubtype, data, type);
            image.ContentId = MimeUtils.GenerateMessageId();
            return "src=\"cid:" + image.ContentId + "\"";
        });

    private static string BodyOf(string html) =>
        BodyContent().Match(html) is { Success: true } body ? body.Groups[1].Value : html;

    // Keep the original's <style> blocks so a quoted newsletter does not lose its layout.
    private static string StylesOf(string html) =>
        string.Concat(StyleBlock().Matches(BodyContent().Match(html) is { Success: true } b ? html[..b.Index] : string.Empty).Select(m => m.Value));

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string QuoteBlock(MimeMessage original)
    {
        var culture = Localization.Texts.Culture;
        var block = new StringBuilder()
            .Append("\n\n")
            .Append("-----Ursprüngliche Nachricht-----\n")
            .Append(CultureInfo.InvariantCulture, $"Von: {Format(original.From.Mailboxes)}\n")
            .Append(CultureInfo.InvariantCulture, $"Gesendet: {original.Date.LocalDateTime.ToString("dddd, d. MMMM yyyy HH:mm", culture)}\n")
            .Append(CultureInfo.InvariantCulture, $"An: {Format(original.To.Mailboxes)}\n");

        if (original.Cc.Mailboxes.Any())
        {
            block.Append(CultureInfo.InvariantCulture, $"Cc: {Format(original.Cc.Mailboxes)}\n");
        }

        return block
            .Append(CultureInfo.InvariantCulture, $"Betreff: {original.Subject}\n\n")
            .Append(PlainTextOf(original).Trim())
            .ToString();
    }

    private static List<MailboxAddress> Distinct(IEnumerable<MailboxAddress> candidates, IEnumerable<string> ownAddresses, IEnumerable<string> exclude)
    {
        var seen = new HashSet<string>(exclude.Concat(ownAddresses), StringComparer.OrdinalIgnoreCase);
        return candidates.Where(m => seen.Add(m.Address)).ToList();
    }

    /// <summary>"Name &lt;address&gt;", quoting the name only where RFC 5322 requires it (e.g. "Muster, Anna").</summary>
    private static string Format(IEnumerable<MailboxAddress> addresses) => string.Join(", ", addresses.Select(Format));

    private static string Format(MailboxAddress address)
    {
        if (string.IsNullOrWhiteSpace(address.Name))
        {
            return address.Address;
        }

        var name = address.Name.IndexOfAny(['(', ')', '<', '>', '[', ']', ':', ';', '@', '\\', ',', '.', '"']) >= 0
            ? "\"" + address.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : address.Name;
        return $"{name} <{address.Address}>";
    }

    [GeneratedRegex(@"^\s*(re|aw|fw|fwd|wg|antw)\s*(\[\d+\])?\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"src=""data:(image/[\w.+-]+);base64,([A-Za-z0-9+/=\s]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DataImage();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyContent();

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlock();
}
