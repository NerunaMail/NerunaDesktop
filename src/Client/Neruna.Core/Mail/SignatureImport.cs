using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>
/// Reads a signature from a file: HTML (e.g. a signature saved by another mail program, with its pictures in a folder
/// next to it), plain text or a mail (.eml). The result is sanitized HTML with pictures as data: URIs, like the editor keeps it.
/// </summary>
public static partial class SignatureImport
{
    public static IReadOnlyList<string> Patterns { get; } = ["*.htm", "*.html", "*.txt", "*.eml"];

    public const int MaxFileBytes = 10 * 1024 * 1024;

    private const int MaxImageBytes = 3 * 1024 * 1024;

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".bmp"] = "image/bmp", [".webp"] = "image/webp",
    };

    /// <summary>Reads the file; pictures it refers to by a relative path are taken from its folder (and below it only).</summary>
    /// <exception cref="InvalidDataException">The file is too large, of another kind or holds nothing usable (message for the user).</exception>
    public static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new InvalidDataException(T("Die Datei ist zu gross für eine Signatur."));
        }

        var folder = Path.GetFullPath(Path.GetDirectoryName(path) ?? ".");
        byte[]? Relative(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(folder, relative));
            var inside = full.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return inside && File.Exists(full) && new FileInfo(full).Length <= MaxImageBytes ? File.ReadAllBytes(full) : null;
        }

        return Read(Path.GetFileName(path), await File.ReadAllBytesAsync(path, cancellationToken), Relative);
    }

    /// <param name="relativeFile">Content of a file the HTML refers to by a relative path, or null if there is none.</param>
    /// <exception cref="InvalidDataException">The file is of another kind or holds nothing usable (message for the user).</exception>
    public static string Read(string fileName, byte[] content, Func<string, byte[]?>? relativeFile = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > MaxFileBytes)
        {
            throw new InvalidDataException(T("Die Datei ist zu gross für eine Signatur."));
        }

        var html = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".htm" or ".html" => FromMessage(new MimeMessage { Body = new TextPart("html") { Text = WithPictures(Decode(content, html: true), relativeFile) } }),
            ".txt" => MessageContent.PlainTextToHtml(Decode(content, html: false).Trim()),
            ".eml" => FromMessage(Load(content)),
            _ => throw new InvalidDataException(T("Signaturen lassen sich aus HTML-, Text- und E-Mail-Dateien (.htm, .html, .txt, .eml) übernehmen.")),
        };

        if (HtmlText.ToPlainText(html).Trim().Length == 0 && !html.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(T("Die Datei enthält nichts, was sich als Signatur übernehmen lässt."));
        }

        return html;
    }

    private static MimeMessage Load(byte[] content)
    {
        try
        {
            return MimeMessage.Load(new MemoryStream(content));
        }
        catch (FormatException)
        {
            throw new InvalidDataException(T("Die Datei ist keine lesbare E-Mail."));
        }
    }

    // Sanitized like a received mail (no scripts, no forms); pictures from the internet stay, they belong to the signature.
    private static string FromMessage(MimeMessage message)
    {
        var html = MessageComposer.InlineImagesAsDataUris(MessageContent.From(message, allowRemoteContent: true));
        return (MessageComposer.StylesOf(html) + MessageComposer.BodyOf(html)).Trim();
    }

    /// <summary>Text with the encoding of its byte order mark, of its meta tag (HTML) or else UTF-8 – Windows-1252 if it is not valid UTF-8.</summary>
    private static string Decode(byte[] content, bool html)
    {
        string? charset = content switch
        {
            [0xEF, 0xBB, 0xBF, ..] => "utf-8",
            [0xFF, 0xFE, ..] => "utf-16",
            [0xFE, 0xFF, ..] => "utf-16BE",
            _ => null,
        };

        if (charset is null && html && MetaCharset().Match(Encoding.Latin1.GetString(content, 0, Math.Min(content.Length, 4096))) is { Success: true } meta)
        {
            charset = meta.Groups[1].Value;
        }

        if (charset is null)
        {
            try
            {
                _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
                charset = "utf-8";
            }
            catch (DecoderFallbackException)
            {
                charset = "windows-1252";
            }
        }

        var part = new TextPart(html ? "html" : "plain") { Content = new MimeContent(new MemoryStream(content)) };
        try
        {
            return part.GetText(charset).TrimStart('﻿');
        }
        catch (ArgumentException)
        {
            return part.GetText("utf-8").TrimStart('﻿');
        }
        catch (NotSupportedException)
        {
            return part.GetText("utf-8").TrimStart('﻿');
        }
    }

    /// <summary>Pictures next to an HTML file ("Signatur-Dateien/image001.png") become data: URIs.</summary>
    private static string WithPictures(string html, Func<string, byte[]?>? relativeFile) => relativeFile is null ? html : PictureSource().Replace(html, match =>
    {
        var value = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["u"].Value;
        if (value.Length == 0 || value.StartsWith("//", StringComparison.Ordinal) || UrlScheme().IsMatch(value))
        {
            return match.Value;
        }

        var relative = System.Net.WebUtility.HtmlDecode(Uri.UnescapeDataString(value.Split('?', '#')[0])).Replace('\\', '/');
        if (!ImageTypes.TryGetValue(Path.GetExtension(relative), out var type) || relativeFile(relative) is not { } bytes)
        {
            return match.Value;
        }

        return $"{match.Groups["a"].Value}=\"data:{type};base64,{Convert.ToBase64String(bytes)}\"";
    });

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?\s*([\w.:-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();

    [GeneratedRegex(@"(?<a>\b(?:src|background))\s*=\s*(?:""(?<q>[^""]*)""|'(?<q>[^']*)'|(?<u>[^\s>""']+))", RegexOptions.IgnoreCase)]
    private static partial Regex PictureSource();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex UrlScheme();
}
