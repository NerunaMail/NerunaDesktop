using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// HTML as A4 pages – for printing and "als PDF speichern": laid out by the renderer of the reading pane, drawn page by
/// page at 192 dpi and put into a PDF. A page ends in an empty line of pixels near its bottom, so no line of text is
/// cut in two. The system's PDF viewer prints it (printer, pages, copies – all in its own dialog).
/// </summary>
/// <summary>A page picture and how many of its pixels make one pixel at 96 dpi (2 for pages drawn at 192 dpi).</summary>
internal sealed record PdfPage(SKBitmap Bitmap, double Density) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

internal static class HtmlPdf
{
    // A4 at 96 dpi; the PDF measures in points (72 per inch).
    private const double ShortSide = 793.7;
    private const double LongSide = 1122.5;
    private const double Margin = 48;
    private const double Scale = 2; // 192 dpi
    private const float PointsPerPixel = 72f / 96f;

    /// <summary>The pages as pictures of their content (without margins and footer) – for the preview, too.</summary>
    public static IReadOnlyList<PdfPage> Render(string html, string stylesheet, bool landscape, IReadOnlyDictionary<string, Bitmap>? images = null)
    {
        var width = (landscape ? LongSide : ShortSide) - (2 * Margin);
        var pageHeight = (landscape ? ShortSide : LongSide) - (2 * Margin) - 24; // room for the footer
        using var container = new HtmlContainer(new Avalonia.Controls.Border()) { AvoidAsyncImagesLoading = true, AvoidImagesLateLoading = true, MaxSize = new Size(width, 0) };
        container.ImageLoad += (_, e) =>
        {
            if (images is not null && images.TryGetValue(e.Src, out var image))
            {
                e.Handled = true;
                e.Callback(image);
            }
            else if (!e.Src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true; // nothing from the network while printing
            }
        };
        container.SetHtml(html, HtmlRender.ParseStyleSheet(stylesheet, true));
        container.PerformLayout();
        var total = Math.Max(container.ActualSize.Height, 1);

        var pages = new List<PdfPage>();
        for (var top = 0.0; top < total - 0.5 && pages.Count < 500;)
        {
            var height = Math.Min(pageHeight, total - top);
            // Twice the pixels at 96 dpi and drawn scaled: a bitmap with 192 dpi made the renderer leave out most text.
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale)));
            using (var context = bitmap.CreateDrawingContext())
            using (context.PushTransform(Matrix.CreateScale(Scale, Scale)))
            {
                context.FillRectangle(Brushes.White, new Rect(0, 0, width, height));
                // The whole plane as "visible" (as the renderer's own RenderToImage does): a narrower clip made it skip
                // most lines of text. The bitmap's size cuts the page out.
                container.ScrollOffset = new Point(0, -top);
                container.PerformPaint(context, new Rect(0, 0, double.MaxValue, double.MaxValue));
            }

            var page = ToSkia(bitmap);
            var rows = top + height >= total - 0.5 ? page.Height : CutRow(page);
            pages.Add(new PdfPage(rows == page.Height ? page : Crop(page, rows), Scale));
            top += rows / Scale;
        }

        return pages;
    }

    /// <summary>A picture (an attached photo) as a page of its own, as large as fits; null if it cannot be read.</summary>
    public static PdfPage? ImagePage(byte[] data)
    {
        var bitmap = SKBitmap.Decode(data);
        return bitmap is null ? null : new PdfPage(bitmap, 1);
    }

    /// <summary>The pages of an attached PDF, drawn at 192 dpi; empty if it cannot be read (e.g. a password).</summary>
    public static IReadOnlyList<PdfPage> PdfPages(byte[] pdf)
    {
        try
        {
            var width = (int)((ShortSide - (2 * Margin)) * Scale);
            var rendering = Neruna.Core.Mail.PdfPages.Render(pdf, 0, 200, width);
            return [.. rendering.Pages.Select(p =>
            {
                var bitmap = new SKBitmap(new SKImageInfo(p.Width, p.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                for (var row = 0; row < p.Height; row++)
                {
                    Marshal.Copy(p.Pixels, row * p.Stride, bitmap.GetPixels() + (row * bitmap.RowBytes), p.Width * 4);
                }

                return new PdfPage(bitmap, Scale);
            })];
        }
        catch (Neruna.Core.Mail.PdfPreviewException)
        {
            return [];
        }
    }

    /// <summary>The pages in a PDF: A4, margins, "Seite 1 von 3" and an optional title at the bottom.</summary>
    public static byte[] ToPdf(IReadOnlyList<PdfPage> pages, bool landscape, string? footer = null)
    {
        var (pageWidth, pageHeight) = landscape ? (LongSide, ShortSide) : (ShortSide, LongSide);
        using var output = new MemoryStream();
        using (var document = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Creator = "Neruna", Producer = "Neruna", RasterDpi = 192 }))
        using (var font = new SKFont(SKTypeface.Default, 8f))
        using (var ink = new SKPaint { Color = new SKColor(0x60, 0x60, 0x60), IsAntialias = true })
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var canvas = document.BeginPage((float)(pageWidth * PointsPerPixel), (float)(pageHeight * PointsPerPixel));
                var page = pages[i].Bitmap;
                var x = (float)(Margin * PointsPerPixel);

                // As large as it is (at 96 dpi), but never larger than the space between the margins.
                var areaWidth = (pageWidth - (2 * Margin)) * PointsPerPixel;
                var areaHeight = (pageHeight - (2 * Margin) - 24) * PointsPerPixel;
                var width = page.Width / pages[i].Density * PointsPerPixel;
                var height = page.Height / pages[i].Density * PointsPerPixel;
                var fit = Math.Min(1, Math.Min(areaWidth / width, areaHeight / height));
                var destination = SKRect.Create(x, x, (float)(width * fit), (float)(height * fit));
                using (var image = SKImage.FromBitmap(page))
                {
                    canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                }

                var baseline = (float)((pageHeight - (Margin / 2)) * PointsPerPixel);
                var number = Localization(i + 1, pages.Count);
                canvas.DrawText(number, (float)((pageWidth - Margin) * PointsPerPixel) - font.MeasureText(number), baseline, font, ink);
                if (!string.IsNullOrWhiteSpace(footer))
                {
                    canvas.DrawText(footer.Length > 90 ? footer[..90] + "…" : footer, x, baseline, font, ink);
                }

                document.EndPage();
            }

            document.Close();
        }

        return output.ToArray();
    }

    private static string Localization(int page, int count) => Neruna.Core.Localization.Texts.F("Seite {0} von {1}", page, count);

    // The last empty line of pixels in the lower quarter of the page: there it can end without cutting text.
    private static int CutRow(SKBitmap page)
    {
        var pixels = page.Pixels;
        for (var row = page.Height - 1; row > page.Height * 3 / 4; row--)
        {
            var empty = true;
            for (var x = 0; x < page.Width && empty; x += 2)
            {
                var c = pixels[(row * page.Width) + x];
                empty = c.Red > 245 && c.Green > 245 && c.Blue > 245;
            }

            if (empty)
            {
                return row + 1;
            }
        }

        return page.Height; // a picture or a table without gaps: cut at the page end
    }

    private static SKBitmap Crop(SKBitmap page, int rows)
    {
        var cropped = new SKBitmap(page.Width, rows, page.ColorType, page.AlphaType);
        page.ExtractSubset(cropped, new SKRectI(0, 0, page.Width, rows));
        var copy = cropped.Copy();
        cropped.Dispose();
        page.Dispose();
        return copy;
    }

    private static SKBitmap ToSkia(RenderTargetBitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var result = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.CopyPixels(new PixelRect(size), result.GetPixels(), result.ByteCount, result.RowBytes);
        return result;
    }
}
