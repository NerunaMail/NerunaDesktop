using System.Globalization;
using System.Text;

namespace Neruna.Providers.Demo;

/// <summary>A one-page PDF with a title and some lines of text, as attachment of the demo mails.</summary>
internal static class DemoPdf
{
    public static byte[] Create(string title, IEnumerable<string> lines)
    {
        var content = new StringBuilder();
        content.Append("0.06 0.42 0.74 rg 56 770 483 3 re f 0 0 0 rg\n");
        content.Append(CultureInfo.InvariantCulture, $"BT /F2 20 Tf 56 735 Td ({Escape(title)}) Tj ET\n");
        var y = 690;
        foreach (var line in lines.SelectMany(Wrap))
        {
            content.Append(CultureInfo.InvariantCulture, $"BT /F1 11 Tf 56 {y} Td ({Escape(line)}) Tj ET\n");
            y -= 18;
        }

        var stream = content.ToString();
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
            $"<< /Length {Latin1(stream).Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Latin1(pdf.ToString()).Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Latin1(pdf.ToString()).Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Latin1(pdf.ToString());
    }

    private static IEnumerable<string> Wrap(string line)
    {
        const int MaxLength = 90;
        var current = new StringBuilder();
        foreach (var word in line.Split(' '))
        {
            if (current.Length > 0 && current.Length + word.Length >= MaxLength)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(current.Length > 0 ? " " : string.Empty).Append(word);
        }

        yield return current.ToString();
    }

    // WinAnsi matches Latin-1 for the umlauts; the rest becomes a hyphen.
    private static string Escape(string text) =>
        new string(text.Select(c => c > 0xFF ? '-' : c).ToArray()).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);
}
