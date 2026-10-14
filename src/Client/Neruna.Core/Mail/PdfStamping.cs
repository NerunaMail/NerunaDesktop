using System.Globalization;
using System.Runtime.InteropServices;

namespace Neruna.Core.Mail;

/// <summary>
/// A stamp written into a PDF page: a frame in the stamp colour with the stamp text in capitals and below it the lines
/// (name, date). Part of the page's content afterwards, so every PDF program shows and prints it.
/// </summary>
/// <param name="X">Where the centre of the stamp goes, from the left edge of the page as shown (0 – 1).</param>
/// <param name="Y">Where the centre of the stamp goes, from the top edge of the page as shown (0 – 1).</param>
/// <param name="Color">"#RRGGBB".</param>
public sealed record PdfStampMark(int Page, double X, double Y, string Title, IReadOnlyList<string> Lines, string Color);

/// <summary>Own text written into a PDF page; <paramref name="X"/>/<paramref name="Y"/> is its top left corner (0 – 1 of the page as shown).</summary>
public sealed record PdfTextMark(int Page, double X, double Y, string Text, string Color, float Size);

public static partial class PdfPages
{
    private const float TitleSize = 15;
    private const float LineSize = 9;
    private const float LineHeight = 11.5f;
    private const float Padding = 6;
    private const float MinStampWidth = 110;

    /// <summary>The PDF with the stamp on its page, as a new file (the original bytes stay as they are).</summary>
    /// <exception cref="PdfPreviewException">The PDF cannot be read or written, or is protected by a password.</exception>
    public static byte[] Stamp(byte[] pdf, PdfStampMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return Edit(pdf, mark.Page, (document, page) => DrawStamp(document, page, mark));
    }

    /// <summary>The PDF with the text on its page, as a new file.</summary>
    /// <exception cref="PdfPreviewException">The PDF cannot be read or written, or is protected by a password.</exception>
    public static byte[] AddText(byte[] pdf, PdfTextMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return Edit(pdf, mark.Page, (document, page) => DrawText(document, page, mark));
    }

    private static byte[] Edit(byte[] pdf, int pageIndex, Action<IntPtr, IntPtr> draw)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        lock (Gate)
        {
            Initialize();
            var buffer = Marshal.AllocHGlobal(pdf.Length);
            try
            {
                Marshal.Copy(pdf, 0, buffer, pdf.Length);
                var document = NativeMethods.FPDF_LoadMemDocument(buffer, pdf.Length, IntPtr.Zero);
                if (document == IntPtr.Zero)
                {
                    throw new PdfPreviewException(NativeMethods.FPDF_GetLastError().Value == PasswordError ? PdfPreviewProblem.Password : PdfPreviewProblem.Unreadable);
                }

                try
                {
                    var page = NativeMethods.FPDF_LoadPage(document, pageIndex);
                    if (page == IntPtr.Zero)
                    {
                        throw new PdfPreviewException(PdfPreviewProblem.Unreadable);
                    }

                    try
                    {
                        draw(document, page);
                        if (NativeMethods.FPDFPage_GenerateContent(page) == 0)
                        {
                            throw new PdfPreviewException(PdfPreviewProblem.Unreadable);
                        }
                    }
                    finally
                    {
                        NativeMethods.FPDF_ClosePage(page);
                    }

                    return Save(document);
                }
                finally
                {
                    NativeMethods.FPDF_CloseDocument(document);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // Where the user clicked, in the page's own coordinates (PDFium accounts for rotation and crop box), and the
    // turn against the page's rotation, so what is added reads upright as the page is shown.
    private static (double X, double Y, double Cos, double Sin) Anchor(IntPtr page, double x, double y)
    {
        var width = NativeMethods.FPDF_GetPageWidthF(page);
        var height = NativeMethods.FPDF_GetPageHeightF(page);
        const int Device = 10000;
        var deviceHeight = width > 0 ? (int)Math.Round(Device * height / width) : Device;
        _ = NativeMethods.FPDF_DeviceToPage(page, 0, 0, Device, deviceHeight, 0,
            (int)Math.Round(Math.Clamp(x, 0, 1) * Device), (int)Math.Round(Math.Clamp(y, 0, 1) * deviceHeight), out var pageX, out var pageY);
        var angle = NativeMethods.FPDFPage_GetRotation(page) * Math.PI / 2;
        return (pageX, pageY, Math.Cos(angle), Math.Sin(angle));
    }

    private static void DrawText(IntPtr document, IntPtr page, PdfTextMark mark)
    {
        var (r, g, b) = ParseColor(mark.Color);
        var (left, top, cos, sin) = Anchor(page, mark.X, mark.Y);
        var baseline = -mark.Size * 0.8;
        foreach (var line in mark.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length > 0)
            {
                var (text, _) = NewText(document, "Helvetica", mark.Size, line, r, g, b);
                NativeMethods.FPDFPageObj_Transform(text, 1, 0, 0, 1, 0, baseline);
                NativeMethods.FPDFPageObj_Transform(text, cos, sin, -sin, cos, left, top);
                NativeMethods.FPDFPage_InsertObject(page, text);
            }

            baseline -= mark.Size * 1.25;
        }
    }

    private static void DrawStamp(IntPtr document, IntPtr page, PdfStampMark mark)
    {
        var (r, g, b) = ParseColor(mark.Color);
        var (centerX, centerY, cos, sin) = Anchor(page, mark.X, mark.Y);

        var texts = new List<(IntPtr Text, float Width)>
        {
            NewText(document, "Helvetica-Bold", TitleSize, mark.Title.ToUpper(CultureInfo.CurrentCulture), r, g, b),
        };
        texts.AddRange(mark.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(line => NewText(document, "Helvetica", LineSize, line, r, g, b)));

        var stampWidth = Math.Max(MinStampWidth, texts.Max(t => t.Width) + (2 * Padding) + 8);
        var stampHeight = (2 * Padding) + TitleSize + ((texts.Count - 1) * LineHeight);

        void Place(IntPtr obj, double x, double y)
        {
            NativeMethods.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, y);
            NativeMethods.FPDFPageObj_Transform(obj, cos, sin, -sin, cos, centerX, centerY);
            NativeMethods.FPDFPage_InsertObject(page, obj);
        }

        var frame = NativeMethods.FPDFPageObj_CreateNewRect(0, 0, stampWidth, stampHeight);
        _ = NativeMethods.FPDFPageObj_SetStrokeColor(frame, r, g, b, 255);
        _ = NativeMethods.FPDFPageObj_SetStrokeWidth(frame, 1.8f);
        _ = NativeMethods.FPDFPath_SetDrawMode(frame, 0, 1);
        Place(frame, -stampWidth / 2, -stampHeight / 2);

        // Title, then the lines below it, each centred.
        var baseline = (stampHeight / 2) - Padding - (TitleSize * 0.75);
        for (var i = 0; i < texts.Count; i++)
        {
            Place(texts[i].Text, -texts[i].Width / 2, baseline);
            baseline -= i == 0 ? (TitleSize * 0.25) + LineHeight : LineHeight;
        }
    }

    private static (IntPtr Text, float Width) NewText(IntPtr document, string font, float size, string text, uint r, uint g, uint b)
    {
        var obj = NativeMethods.FPDFPageObj_NewTextObj(document, font, size);
        if (obj == IntPtr.Zero)
        {
            throw new PdfPreviewException(PdfPreviewProblem.Unreadable);
        }

        _ = NativeMethods.FPDFText_SetText(obj, text);
        _ = NativeMethods.FPDFPageObj_SetFillColor(obj, r, g, b, 255);
        _ = NativeMethods.FPDFPageObj_GetBounds(obj, out var left, out _, out var right, out _);
        return (obj, Math.Max(0, right - left));
    }

    private static (uint R, uint G, uint B) ParseColor(string color)
    {
        var hex = color.TrimStart('#');
        return hex.Length >= 6 && uint.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)
            : (0xC4u, 0x2Bu, 0x1Cu);
    }

    // PDFium writes the document block by block through a callback; collected here (under the lock).
    private static MemoryStream? _saving;
    private static readonly NativeMethods.WriteBlockCallback WriteBlock = OnWriteBlock;

    private static int OnWriteBlock(IntPtr self, IntPtr data, CULong size)
    {
        var length = checked((int)size.Value);
        var chunk = new byte[length];
        Marshal.Copy(data, chunk, 0, length);
        _saving?.Write(chunk);
        return 1;
    }

    private static byte[] Save(IntPtr document)
    {
        _saving = new MemoryStream();
        try
        {
            var writer = new NativeMethods.FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(WriteBlock) };
            if (NativeMethods.FPDF_SaveAsCopy(document, ref writer, new CULong(0)) == 0)
            {
                throw new PdfPreviewException(PdfPreviewProblem.Unreadable);
            }

            return _saving.ToArray();
        }
        finally
        {
            _saving.Dispose();
            _saving = null;
        }
    }

    private static partial class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int WriteBlockCallback(IntPtr self, IntPtr data, CULong size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileWrite
        {
            public int Version;
            public IntPtr WriteBlock;
        }

        [DllImport(Pdfium)]
        internal static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);

        [DllImport(Pdfium)]
        internal static extern int FPDFPage_GetRotation(IntPtr page);

        [DllImport(Pdfium, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        internal static extern IntPtr FPDFPageObj_NewTextObj(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string font, float size);

        [DllImport(Pdfium)]
        internal static extern int FPDFText_SetText(IntPtr textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);

        [DllImport(Pdfium)]
        internal static extern IntPtr FPDFPageObj_CreateNewRect(float x, float y, float width, float height);

        [DllImport(Pdfium)]
        internal static extern int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);

        [DllImport(Pdfium)]
        internal static extern int FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);

        [DllImport(Pdfium)]
        internal static extern int FPDFPageObj_SetStrokeWidth(IntPtr obj, float width);

        [DllImport(Pdfium)]
        internal static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);

        [DllImport(Pdfium)]
        internal static extern int FPDFPageObj_GetBounds(IntPtr obj, out float left, out float bottom, out float right, out float top);

        [DllImport(Pdfium)]
        internal static extern void FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);

        [DllImport(Pdfium)]
        internal static extern void FPDFPage_InsertObject(IntPtr page, IntPtr obj);

        [DllImport(Pdfium)]
        internal static extern int FPDFPage_GenerateContent(IntPtr page);

        [DllImport(Pdfium)]
        internal static extern int FPDF_SaveAsCopy(IntPtr document, ref FileWrite writer, CULong flags);
    }
}
