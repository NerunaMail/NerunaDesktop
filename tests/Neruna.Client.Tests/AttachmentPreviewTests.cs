using System.Globalization;
using System.Text;
using MimeKit;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class AttachmentPreviewTests
{
    [Theory]
    [InlineData("application/octet-stream", "Rechnung.PDF", AttachmentPreviewKind.Pdf)]
    [InlineData("application/pdf", "anhang", AttachmentPreviewKind.Pdf)]
    [InlineData("application/octet-stream", "foto.jpeg", AttachmentPreviewKind.Image)]
    [InlineData("image/png", "bild", AttachmentPreviewKind.Image)]
    [InlineData("image/svg+xml", "logo.svg", AttachmentPreviewKind.None)]
    [InlineData("image/heic", "IMG_0001.HEIC", AttachmentPreviewKind.None)]
    [InlineData("application/octet-stream", "setup.exe", AttachmentPreviewKind.None)]
    [InlineData("application/octet-stream", "Weitergeleitet.eml", AttachmentPreviewKind.Message)]
    [InlineData("message/rfc822", "anhang", AttachmentPreviewKind.Message)]
    public void KindByNameOrType(string mimeType, string fileName, AttachmentPreviewKind expected) =>
        Assert.Equal(expected, AttachmentPreview.KindOf(new MimePart(ContentType.Parse(mimeType)), fileName));

    [Fact]
    public void AnAttachedMessageIsShownAsAMessage() =>
        Assert.Equal(AttachmentPreviewKind.Message, AttachmentPreview.KindOf(new MessagePart(), "mail.pdf"));

    [Fact]
    public void RendersPdfPages()
    {
        // Two A4 pages; the first has a black square at the bottom left (PDF origin), the second is empty.
        var rendering = PdfPages.Render(MinimalPdf("0 0 100 100 re f", ""), 0, 10, 200);

        Assert.Equal(2, rendering.PageCount);
        Assert.Equal(2, rendering.Pages.Count);
        var page = rendering.Pages[0];
        Assert.Equal(200, page.Width);
        Assert.Equal(283, page.Height); // 200 × 842 / 595
        Assert.Equal(0, Pixel(page, 5, page.Height - 5));
        Assert.Equal(255, Pixel(page, page.Width - 5, 5));
        Assert.All(rendering.Pages[1].Pixels, b => Assert.Equal(255, b));
    }

    [Fact]
    public void ShowsWhatWasFilledInAForm()
    {
        // Many programs save a filled-in form with the values only and leave drawing them to the viewer
        // (NeedAppearances): without the form environment the page stayed white.
        var rendering = PdfPages.Render(FormPdf("(MMMMMMMMMM)"), 0, 1, 595);

        var page = rendering.Pages[0];
        var field = Enumerable.Range(60, 300).SelectMany(x => Enumerable.Range(842 - 735, 30).Select(y => Pixel(page, x, y)));
        Assert.Contains(field, b => b < 128);
    }

    [Fact]
    public void RendersOnlyTheRequestedPages()
    {
        var rendering = PdfPages.Render(MinimalPdf("", ""), 1, 10, 50);

        Assert.Equal(2, rendering.PageCount);
        Assert.Single(rendering.Pages);
    }

    [Fact]
    public void RejectsWhatIsNoPdf()
    {
        var error = Assert.Throws<PdfPreviewException>(() => PdfPages.Render(Encoding.ASCII.GetBytes("no pdf at all"), 0, 1, 100));
        Assert.Equal(PdfPreviewProblem.Unreadable, error.Problem);
    }

    [Fact]
    public void StampsWhereClickedInItsColour()
    {
        // Top right of the second (empty) page, in red.
        var stamped = PdfPages.Stamp(MinimalPdf("", ""), new PdfStampMark(1, 0.75, 0.2, "Genehmigt", ["Anna Muster", "14.10.2026 15:42"], "#FF0000"));
        var rendering = PdfPages.Render(stamped, 0, 10, 400);

        Assert.Equal(2, rendering.PageCount);
        Assert.All(rendering.Pages[0].Pixels, b => Assert.Equal(255, b)); // the first page stays as it was
        var page = rendering.Pages[1];
        var red = Enumerable.Range(0, page.Height).SelectMany(y => Enumerable.Range(0, page.Width).Select(x => (x, y)))
            .Where(p => IsRed(page, p.x, p.y)).ToList();
        Assert.NotEmpty(red);
        Assert.InRange(red.Average(p => p.x), 0.70 * page.Width, 0.80 * page.Width);
        Assert.InRange(red.Average(p => p.y), 0.17 * page.Height, 0.23 * page.Height);
    }

    [Fact]
    public void WritesOwnTextBelowAndRightOfTheClick()
    {
        var edited = PdfPages.AddText(MinimalPdf(""), new PdfTextMark(0, 0.1, 0.5, "Bezahlt am 14.10.\nVisum AM", "#FF0000", 14));
        var page = PdfPages.Render(edited, 0, 1, 400).Pages[0];
        var red = Enumerable.Range(0, page.Height).SelectMany(y => Enumerable.Range(0, page.Width).Select(x => (x, y)))
            .Where(p => IsRed(page, p.x, p.y)).ToList();
        Assert.NotEmpty(red);
        Assert.InRange(red.Min(p => p.x), 0.09 * page.Width, 0.13 * page.Width);
        Assert.InRange(red.Min(p => p.y), 0.49 * page.Height, 0.52 * page.Height);
        Assert.True(red.Max(p => p.y) > 0.53 * page.Height); // two lines
    }

    [Fact]
    public void StampingKeepsTheOriginal()
    {
        var original = MinimalPdf("0 0 100 100 re f");
        var copy = original.ToArray();
        _ = PdfPages.Stamp(original, new PdfStampMark(0, 0.5, 0.5, "Geprüft", [], "#0F6CBD"));
        Assert.Equal(copy, original);
    }

    private static bool IsRed(PdfPageImage page, int x, int y)
    {
        var i = (y * page.Stride) + (x * 4); // BGRA
        return page.Pixels[i + 2] > 200 && page.Pixels[i + 1] < 120 && page.Pixels[i] < 120;
    }

    private static byte Pixel(PdfPageImage page, int x, int y) => page.Pixels[(y * page.Stride) + (x * 4)];

    internal static byte[] MinimalPdf(params string[] pageContents)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = string.Join(' ', pageContents.Select((_, i) => $"{3 + (i * 2)} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pageContents.Length} >>");
        foreach (var (content, i) in pageContents.Select((c, i) => (c, i)))
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {4 + (i * 2)} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        return Assemble(objects);
    }

    /// <summary>One empty A4 page with a text field holding <paramref name="value"/> – no appearance stream.</summary>
    private static byte[] FormPdf(string value) => Assemble(
    [
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Annots [5 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V {value} /Rect [50 700 450 740] /F 4 /P 3 0 R /DA (/Helv 24 Tf 0 g) >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]);

    private static byte[] Assemble(List<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
