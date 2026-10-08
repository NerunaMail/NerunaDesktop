using SkiaSharp;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Turns any picture into a contact photo: centered square crop, 256×256, JPEG (small enough for a vCard).</summary>
internal static class AvatarImage
{
    private const int Size = 256;

    /// <returns>JPEG data, or null if the file is no readable image.</returns>
    public static byte[]? FromFile(string path)
    {
        using var source = SKBitmap.Decode(path);
        if (source is null)
        {
            return null;
        }

        var side = Math.Min(source.Width, source.Height);
        var crop = new SKRectI((source.Width - side) / 2, (source.Height - side) / 2, (source.Width + side) / 2, (source.Height + side) / 2);
        using var square = new SKBitmap(side, side);
        if (!source.ExtractSubset(square, crop))
        {
            return null;
        }

        using var scaled = square.Resize(new SKImageInfo(Size, Size), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(scaled);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return jpeg.ToArray();
    }
}
