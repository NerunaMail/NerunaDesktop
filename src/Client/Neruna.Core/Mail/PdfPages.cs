using System.Runtime.InteropServices;

namespace Neruna.Core.Mail;

/// <summary>One rendered page: BGRA pixels, premultiplied, white background.</summary>
/// <param name="PointWidth">The page's width in PDF points (1/72 inch), to show things at their size on the page.</param>
public sealed record PdfPageImage(int Width, int Height, int Stride, byte[] Pixels, double PointWidth = 0);

/// <summary>The pages of a PDF as images, for the attachment preview.</summary>
public sealed record PdfRendering(int PageCount, IReadOnlyList<PdfPageImage> Pages);

/// <summary>
/// Renders PDF pages with PDFium (the PDF engine of Chromium; native library from the bblanchon.PDFium packages).
/// Only pictures of the pages: no scripts, no forms, no links. PDFium is not thread-safe, so every call holds a lock.
/// </summary>
public static partial class PdfPages
{
    private const int MaxHeight = 6000;
    private const uint White = 0xFFFFFFFF;
    private const int RenderAnnotations = 0x01;
    private const int PasswordError = 4;

    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>Whether the PDF needs a password to be opened (an owner password that only restricts printing does not count).</summary>
    public static bool NeedsPassword(byte[] pdf) => !Opens(pdf, null, out var passwordError) && passwordError;

    /// <summary>Whether the PDF opens with <paramref name="password"/> (null: without one).</summary>
    public static bool Opens(byte[] pdf, string? password) => Opens(pdf, password, out _);

    private static bool Opens(byte[] pdf, string? password, out bool passwordError)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        lock (Gate)
        {
            Initialize();
            var buffer = Marshal.AllocHGlobal(pdf.Length);
            var secret = password is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(password);
            try
            {
                Marshal.Copy(pdf, 0, buffer, pdf.Length);
                var document = NativeMethods.FPDF_LoadMemDocument(buffer, pdf.Length, secret);
                if (document == IntPtr.Zero)
                {
                    passwordError = NativeMethods.FPDF_GetLastError().Value == PasswordError;
                    return false;
                }

                NativeMethods.FPDF_CloseDocument(document);
                passwordError = false;
                return true;
            }
            finally
            {
                if (secret != IntPtr.Zero)
                {
                    Marshal.ZeroFreeCoTaskMemUTF8(secret);
                }

                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Renders the pages <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> − 1 (as far as
    /// they exist) at <paramref name="width"/> pixels.
    /// </summary>
    /// <exception cref="PdfPreviewException">The PDF cannot be read, is protected by a password, or PDFium is missing.</exception>
    public static PdfRendering Render(byte[] pdf, int first, int count, int width)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

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
                    var pageCount = NativeMethods.FPDF_GetPageCount(document);
                    var pages = new List<PdfPageImage>();
                    for (var index = first; index < Math.Min(pageCount, first + count); index++)
                    {
                        pages.Add(RenderPage(document, index, width));
                    }

                    return new PdfRendering(pageCount, pages);
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

    private static PdfPageImage RenderPage(IntPtr document, int index, int width)
    {
        var page = NativeMethods.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero)
        {
            throw new PdfPreviewException(PdfPreviewProblem.Unreadable);
        }

        try
        {
            var pageWidth = NativeMethods.FPDF_GetPageWidthF(page);
            var pageHeight = NativeMethods.FPDF_GetPageHeightF(page);
            var height = pageWidth > 0 ? (int)Math.Round(width * pageHeight / pageWidth) : width;
            height = Math.Clamp(height, 1, MaxHeight);

            var bitmap = NativeMethods.FPDFBitmap_Create(width, height, 1);
            if (bitmap == IntPtr.Zero)
            {
                throw new PdfPreviewException(PdfPreviewProblem.Unreadable);
            }

            try
            {
                _ = NativeMethods.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, White);
                NativeMethods.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, RenderAnnotations);
                var stride = NativeMethods.FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[stride * height];
                Marshal.Copy(NativeMethods.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
                return new PdfPageImage(width, height, stride, pixels, pageWidth);
            }
            finally
            {
                NativeMethods.FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            NativeMethods.FPDF_ClosePage(page);
        }
    }

    private static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            NativeMethods.FPDF_InitLibrary();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new PdfPreviewException(PdfPreviewProblem.Unavailable, ex);
        }

        _initialized = true;
    }

    private static partial class NativeMethods
    {
        private const string Pdfium = "pdfium";

        [DllImport(Pdfium)]
        internal static extern void FPDF_InitLibrary();

        [DllImport(Pdfium)]
        internal static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, IntPtr password);

        [DllImport(Pdfium)]
        internal static extern CULong FPDF_GetLastError();

        [DllImport(Pdfium)]
        internal static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(Pdfium)]
        internal static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(Pdfium)]
        internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);

        [DllImport(Pdfium)]
        internal static extern void FPDF_ClosePage(IntPtr page);

        [DllImport(Pdfium)]
        internal static extern float FPDF_GetPageWidthF(IntPtr page);

        [DllImport(Pdfium)]
        internal static extern float FPDF_GetPageHeightF(IntPtr page);

        [DllImport(Pdfium)]
        internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

        [DllImport(Pdfium)]
        internal static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport(Pdfium)]
        internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

        [DllImport(Pdfium)]
        internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

        [DllImport(Pdfium)]
        internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);

        [DllImport(Pdfium)]
        internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    }
}

public enum PdfPreviewProblem
{
    /// <summary>Damaged or not a PDF.</summary>
    Unreadable,

    /// <summary>Protected by a password.</summary>
    Password,

    /// <summary>PDFium is not available on this system.</summary>
    Unavailable,
}

public sealed class PdfPreviewException : Exception
{
    public PdfPreviewException(PdfPreviewProblem problem, Exception? inner = null)
        : base("PDF preview: " + problem, inner) => Problem = problem;

    public PdfPreviewException()
        : this(PdfPreviewProblem.Unreadable)
    {
    }

    public PdfPreviewException(string message)
        : base(message)
    {
    }

    public PdfPreviewException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public PdfPreviewProblem Problem { get; }
}
