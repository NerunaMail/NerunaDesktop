using SkiaSharp;

namespace Neruna.Desktop.Editor;

/// <summary>What an image to insert is: size, format and whether it suits a mail as it is.</summary>
internal sealed record ImageInfo(int Width, int Height, string MimeType, long Bytes)
{
    /// <summary>A format every mail program shows (BMP, ICO … are not).</summary>
    public bool IsWebFormat => MimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    /// <summary>Too large for a signature or a mail: wider than a mail, heavy, or in a format not meant for mail.</summary>
    public bool IsLarge => Width > InsertedImages.LargeWidth || Bytes > InsertedImages.LargeBytes || !IsWebFormat;

    /// <summary>Usable unchanged (the editor takes up to 2 MB of a mail-friendly format).</summary>
    public bool CanKeep => IsWebFormat && Bytes <= InsertedImages.MaxBytes;
}

/// <summary>
/// Pictures inserted into the editor (logo in a signature, a screenshot): checked, and on request shrunk to a sensible
/// width as PNG – a 4K bitmap would make every mail several megabytes.
/// </summary>
internal static class InsertedImages
{
    /// <summary>Width offered when shrinking (a logo or photo in a signature).</summary>
    public const int SuggestedWidth = 300;

    public const int LargeWidth = 800;

    public const long LargeBytes = 400 * 1024;

    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Null if the data is no image Neruna can read.</summary>
    public static ImageInfo? Inspect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null)
        {
            return null;
        }

        var mime = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Gif => "image/gif",
            SKEncodedImageFormat.Webp => "image/webp",
            SKEncodedImageFormat.Bmp => "image/bmp",
            SKEncodedImageFormat.Ico => "image/x-icon",
            _ => "application/octet-stream",
        };
        return new ImageInfo(codec.Info.Width, codec.Info.Height, mime, data.Length);
    }

    /// <summary>The picture as PNG, at most <paramref name="width"/> pixels wide (never enlarged), same proportions.</summary>
    public static byte[] ShrinkToPng(byte[] data, int width)
    {
        using var original = SKBitmap.Decode(data) ?? throw new InvalidDataException("Not an image.");
        var target = Math.Min(width, original.Width);
        var height = Math.Max(1, (int)Math.Round(original.Height * (double)target / original.Width));
        using var resized = original.Resize(new SKImageInfo(target, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
                            ?? throw new InvalidDataException("Could not resize the image.");
        using var image = SKImage.FromBitmap(resized);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    public static string SizeText(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
