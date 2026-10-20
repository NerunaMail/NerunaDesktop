using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using MimeKit;
using Neruna.Core;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// A mail as a file and on paper: "Speichern unter …" as EML (as it came) or PDF (to archive – with its pictures and
/// PDFs as pages after it, beside it or not at all, Einstellungen → E-Mail), and "Drucken" through a PDF that the
/// system's viewer prints. The format chosen last is offered first next time.
/// </summary>
internal static partial class MailExport
{
    public const string Eml = ".eml";
    public const string Pdf = ".pdf";

    /// <summary>Set at start; the remembered format and the attachment choice live there.</summary>
    public static ISettingsStore? Settings { get; set; }

    public static async Task<string> LastFormatAsync() =>
        Settings is not null && await Settings.GetAsync(SettingKeys.MailSaveFormat) == "pdf" ? Pdf : Eml;

    private static async Task<string> AttachmentModeAsync()
    {
        var mode = Settings is null ? null : await Settings.GetAsync(SettingKeys.MailPdfAttachments);
        return mode is "beside" or "none" ? mode : "inside";
    }

    /// <summary>A file name from the subject (no characters a file system refuses).</summary>
    public static string NameOf(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? T("E-Mail") : message.Subject.Trim();
        return QuickActionService.SafeFileName(subject[..Math.Min(subject.Length, 120)]);
    }

    /// <summary>"Speichern unter …" for one mail: EML or PDF, the format of last time offered first.</summary>
    /// <returns>A status text, or null when cancelled.</returns>
    public static async Task<string?> SaveAsync(MimeMessage message, IFileService files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var eml = (T("E-Mail-Datei (EML)"), Eml);
        var pdf = (T("PDF-Dokument"), Pdf);
        var types = await LastFormatAsync() == Pdf ? new[] { pdf, eml } : [eml, pdf];
        if (await files.SaveFileAsAsync(T("E-Mail speichern"), NameOf(message), types) is not { } target)
        {
            return null;
        }

        await using (target.Stream)
        {
            await WriteAsync(message, target.Extension, target.Stream, target.Path is { } path ? Path.GetDirectoryName(path) : null);
        }

        await RememberAsync(target.Extension);
        return T("E-Mail gespeichert.");
    }

    /// <summary>Several mails into a folder, each under its subject, in the format of last time.</summary>
    public static async Task<int> SaveAllAsync(IReadOnlyList<MimeMessage> messages, string folder)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var extension = await LastFormatAsync();
        foreach (var message in messages)
        {
            var path = Unique(folder, NameOf(message), extension);
            await using var stream = File.Create(path);
            await WriteAsync(message, extension, stream, folder);
        }

        return messages.Count;
    }

    /// <summary>Printing: the mail (without its attachments) as a PDF, opened in the system's viewer to print from there.</summary>
    public static Task PrintAsync(MimeMessage message, IFileService files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var pdf = ToPdf(message, attachments: false);
        return files.OpenAsync(NameOf(message) + Pdf, stream => stream.WriteAsync(pdf).AsTask());
    }

    private static async Task WriteAsync(MimeMessage message, string extension, Stream stream, string? folder)
    {
        if (extension != Pdf)
        {
            await message.WriteToAsync(stream);
            return;
        }

        var mode = await AttachmentModeAsync();
        await stream.WriteAsync(ToPdf(message, attachments: mode == "inside"));
        if (mode == "beside" && folder is not null)
        {
            foreach (var part in message.Attachments.OfType<MimePart>())
            {
                var name = MessageContent.FileNameOf(part);
                await using var file = File.Create(Unique(folder, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name)));
                if (part.Content is { } data)
                {
                    await data.DecodeToAsync(file);
                }
            }
        }
    }

    private static Task RememberAsync(string extension) =>
        Settings?.SetAsync(SettingKeys.MailSaveFormat, extension == Pdf ? "pdf" : "eml") ?? Task.CompletedTask;

    /// <summary>
    /// The mail as PDF: a head with subject, sender, recipients, date and attachments, then the text as it is shown
    /// (pictures from the message; nothing from the internet) – and, if wanted, attached pictures and PDFs as pages.
    /// </summary>
    public static byte[] ToPdf(MimeMessage message, bool attachments)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = MessageContent.From(message, forRenderer: true);
        var images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        foreach (Match match in ImageSource().Matches(content.Html))
        {
            var src = WebUtility.HtmlDecode(match.Groups["src"].Value);
            if (!images.ContainsKey(src) && MessageContent.ContentIdOf(src) is { } id && content.InlineParts.TryGetValue(id, out var part) && part.Content is { } data)
            {
                try
                {
                    using var buffer = new MemoryStream();
                    data.DecodeTo(buffer);
                    buffer.Position = 0;
                    images[src] = new Bitmap(buffer);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
                {
                    // not a picture the renderer can read: left out
                }
            }
        }

        // The head goes into the mail's own <body> – two documents in a row and the renderer drops the second.
        var body = MailPaper.Prepare(content.Html, darkTheme: false).Html;
        var open = BodyTag().Match(body);
        var html = open.Success ? body.Insert(open.Index + open.Length, Head(message, content)) : Head(message, content) + body;
        var pages = new List<PdfPage>(HtmlPdf.Render(html, MailPaper.LightStylesheet + HeadStyle, landscape: false, images));
        try
        {
            if (attachments)
            {
                foreach (var part in message.Attachments.OfType<MimePart>())
                {
                    using var buffer = new MemoryStream();
                    part.Content?.DecodeTo(buffer);
                    var bytes = buffer.ToArray();
                    if (part.ContentType.IsMimeType("application", "pdf") || MessageContent.FileNameOf(part).EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        pages.AddRange(HtmlPdf.PdfPages(bytes));
                    }
                    else if (part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) && HtmlPdf.ImagePage(bytes) is { } picture)
                    {
                        pages.Add(picture);
                    }
                }
            }

            return HtmlPdf.ToPdf(pages, landscape: false, message.Subject);
        }
        finally
        {
            foreach (var page in pages)
            {
                page.Dispose();
            }

            foreach (var image in images.Values)
            {
                image.Dispose();
            }
        }
    }

    private const string HeadStyle =
        " .neruna-head { border-bottom: 1px solid #C8C8C8; margin-bottom: 14px; padding-bottom: 8px; }" +
        " .neruna-head h1 { font-size: 15pt; margin: 0 0 8px 0; } .neruna-head td { font-size: 9.5pt; padding: 1px 8px 1px 0; vertical-align: top; }" +
        " .neruna-head td.key { color: #606060; white-space: nowrap; }";

    private static string Head(MimeMessage message, MessageContent content)
    {
        static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
        var head = new StringBuilder("<div class=\"neruna-head\"><h1>").Append(Encode(message.Subject)).Append("</h1><table>");
        void Row(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                head.Append("<tr><td class=\"key\">").Append(Encode(key)).Append("</td><td>").Append(Encode(value)).Append("</td></tr>");
            }
        }

        Row(T("Von"), message.From.ToString());
        Row(T("An"), message.To.ToString());
        Row(T("Cc"), message.Cc.ToString());
        Row(T("Datum"), message.Date == DateTimeOffset.MinValue ? null : message.Date.ToLocalTime().ToString("f", CultureInfo.CurrentCulture));
        Row(T("Anhänge"), string.Join(", ", content.Attachments.Select(MessageContent.FileNameOf)));
        return head.Append("</table></div>").ToString();
    }

    private static string Unique(string folder, string name, string extension)
    {
        var path = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        }

        return path;
    }

    [GeneratedRegex(@"<img\b[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSource();

    [GeneratedRegex(@"<body\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyTag();
}
