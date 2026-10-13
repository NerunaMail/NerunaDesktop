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
    public void KindByNameOrType(string mimeType, string fileName, AttachmentPreviewKind expected) =>
        Assert.Equal(expected, AttachmentPreview.KindOf(new MimePart(ContentType.Parse(mimeType)), fileName));

    [Fact]
    public void AttachedMessageHasNoPreview() =>
        Assert.Equal(AttachmentPreviewKind.None, AttachmentPreview.KindOf(new MessagePart(), "mail.pdf"));

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

    private static byte Pixel(PdfPageImage page, int x, int y) => page.Pixels[(y * page.Stride) + (x * 4)];

    private static byte[] MinimalPdf(params string[] pageContents)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = string.Join(' ', pageContents.Select((_, i) => $"{3 + (i * 2)} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pageContents.Length} >>");
        foreach (var (content, i) in pageContents.Select((c, i) => (c, i)))
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {4 + (i * 2)} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

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
