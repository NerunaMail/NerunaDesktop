using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace Neruna.Providers.Pst;

/// <summary>An address as the data file has it; without "@" (an Exchange address) only the name counts.</summary>
internal sealed record PstAddress(string? Name, string? Address);

internal sealed record PstAttachment(string FileName, byte[] Content, string? ContentId = null, bool IsInline = false, PstItem? Message = null);

/// <summary>A message of a data file, as far as it becomes MIME: the original headers when the file kept them.</summary>
internal sealed record PstItem(
    string? Headers,
    string? Subject,
    PstAddress? From,
    IReadOnlyList<PstAddress> To,
    IReadOnlyList<PstAddress> Cc,
    IReadOnlyList<PstAddress> Bcc,
    DateTimeOffset? Date,
    string? MessageId,
    string? InReplyTo,
    string? Html,
    string? Text,
    string? Rtf,
    IReadOnlyList<PstAttachment> Attachments);

/// <summary>Outlook keeps messages as properties, not as MIME: this puts them together again.</summary>
internal static class PstMime
{
    public static MimeMessage Build(PstItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var message = new MimeMessage();
        message.Headers.Clear();
        if (!string.IsNullOrWhiteSpace(item.Headers))
        {
            // As received – except what describes the body: that is built anew below.
            foreach (var header in ParseHeaders(item.Headers))
            {
                if (!header.Field.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                    && !header.Field.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase))
                {
                    message.Headers.Add(header);
                }
            }
        }

        if (message.From.Count == 0 && Mailbox(item.From) is { } from)
        {
            message.From.Add(from);
        }

        if (message.To.Count == 0 && message.Cc.Count == 0)
        {
            message.To.AddRange(item.To.Select(Mailbox).OfType<MailboxAddress>());
            message.Cc.AddRange(item.Cc.Select(Mailbox).OfType<MailboxAddress>());
        }

        if (message.Bcc.Count == 0)
        {
            message.Bcc.AddRange(item.Bcc.Select(Mailbox).OfType<MailboxAddress>());
        }

        if (!message.Headers.Contains(HeaderId.Subject) && item.Subject is { } subject)
        {
            message.Subject = subject;
        }

        if (!message.Headers.Contains(HeaderId.Date) && item.Date is { } date)
        {
            message.Date = date;
        }

        if (string.IsNullOrEmpty(message.MessageId) && Id(item.MessageId) is { } id)
        {
            message.MessageId = id;
        }

        if (string.IsNullOrEmpty(message.InReplyTo) && Id(item.InReplyTo) is { } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
        }

        message.Body = Body(item);
        return message;
    }

    private static MimeEntity Body(PstItem item)
    {
        var html = item.Html ?? RtfBody.HtmlOf(item.Rtf);
        var text = item.Text ?? (html is null ? RtfBody.TextOf(item.Rtf) : null);

        // Pictures the HTML shows by "cid:" go with it; everything else is an attachment.
        var inline = html is null ? [] : item.Attachments
            .Where(a => a.Message is null && a.ContentId is { Length: > 0 } cid && html.Contains("cid:" + cid.Trim('<', '>'), StringComparison.OrdinalIgnoreCase))
            .ToList();
        MimeEntity? htmlPart = null;
        if (html is not null)
        {
            htmlPart = new TextPart("html") { Text = html };
            if (inline.Count > 0)
            {
                var related = new MultipartRelated { htmlPart };
                foreach (var attachment in inline)
                {
                    var part = Part(attachment);
                    part.ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = attachment.FileName };
                    related.Add(part);
                }

                htmlPart = related;
            }
        }

        MimeEntity content = (text, htmlPart) switch
        {
            ({ } plain, { } rich) => new MultipartAlternative { new TextPart("plain") { Text = plain }, rich },
            (_, { } rich) => rich,
            ({ } plain, _) => new TextPart("plain") { Text = plain },
            _ => new TextPart("plain") { Text = string.Empty },
        };

        var attachments = item.Attachments.Except(inline).ToList();
        if (attachments.Count == 0)
        {
            return content;
        }

        var mixed = new Multipart("mixed") { content };
        foreach (var attachment in attachments)
        {
            if (attachment.Message is { } attached)
            {
                mixed.Add(new MessagePart { Message = Build(attached), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) });
                continue;
            }

            var part = Part(attachment);
            part.ContentDisposition = new ContentDisposition(attachment.IsInline ? ContentDisposition.Inline : ContentDisposition.Attachment) { FileName = attachment.FileName };
            mixed.Add(part);
        }

        return mixed;
    }

    private static MimePart Part(PstAttachment attachment)
    {
        var part = new MimePart(MimeTypes.GetMimeType(attachment.FileName))
        {
            Content = new MimeContent(new MemoryStream(attachment.Content)),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = attachment.FileName,
        };
        if (attachment.ContentId is { Length: > 0 } cid)
        {
            part.ContentId = cid.Trim('<', '>');
        }

        return part;
    }

    private static List<Header> ParseHeaders(string headers)
    {
        // The stored block ends without the empty line that ends a header block.
        var text = headers.TrimEnd('\r', '\n') + "\r\n\r\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        try
        {
            return [.. HeaderList.Load(stream)];
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static MailboxAddress? Mailbox(PstAddress? address)
    {
        if (address is null || (string.IsNullOrWhiteSpace(address.Address) && string.IsNullOrWhiteSpace(address.Name)))
        {
            return null;
        }

        var email = address.Address is { } a && a.Contains('@', StringComparison.Ordinal) ? a.Trim() : string.Empty;
        return new MailboxAddress(string.IsNullOrWhiteSpace(address.Name) ? null : address.Name.Trim(), email);
    }

    private static string? Id(string? id) => string.IsNullOrWhiteSpace(id) ? null : MimeUtils.EnumerateReferences(id).FirstOrDefault() ?? id.Trim().Trim('<', '>');
}
