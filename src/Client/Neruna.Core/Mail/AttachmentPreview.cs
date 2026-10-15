using MimeKit;

namespace Neruna.Core.Mail;

public enum AttachmentPreviewKind
{
    None,
    Image,
    Pdf,

    /// <summary>A mail (.eml, message/rfc822): shown like a message, with its own attachments.</summary>
    Message,
}

/// <summary>Which attachments the reading pane can show itself (instead of opening them in another program).</summary>
public static class AttachmentPreview
{
    /// <summary>Larger attachments are only opened or saved.</summary>
    public const long MaxBytes = 50 * 1024 * 1024;

    // What the image decoder of the UI reads (Skia): no HEIC, no TIFF, no SVG (SVG can carry scripts and links).
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { "png", "jpeg", "jpg", "pjpeg", "gif", "bmp", "webp" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp" };

    /// <summary>
    /// By the file name first – many mailers send everything as application/octet-stream – then by the MIME type. The
    /// renderers cope with content that does not match.
    /// </summary>
    public static AttachmentPreviewKind KindOf(MimeEntity entity, string fileName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is MessagePart)
        {
            return AttachmentPreviewKind.Message;
        }

        if (entity is not MimePart)
        {
            return AttachmentPreviewKind.None;
        }

        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (extension.Equals(".eml", StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentPreviewKind.Message;
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentPreviewKind.Pdf;
        }

        if (ImageExtensions.Contains(extension))
        {
            return AttachmentPreviewKind.Image;
        }

        return KindOfType(entity.ContentType);
    }

    private static AttachmentPreviewKind KindOfType(ContentType type) =>
        type.IsMimeType("application", "pdf") ? AttachmentPreviewKind.Pdf
        : type.IsMimeType("message", "rfc822") ? AttachmentPreviewKind.Message
        : type.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) && ImageTypes.Contains(type.MediaSubtype) ? AttachmentPreviewKind.Image
        : AttachmentPreviewKind.None;
}
