using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// An image or PDF attachment shown in the reading pane instead of the message, to check it without opening it in
/// another program. PDF pages are rendered in batches – a page as picture takes several megabytes.
/// </summary>
internal sealed partial class AttachmentPreviewViewModel : ObservableObject
{
    private const int PageBatch = 5;
    private const int PdfWidth = 1100;
    private const int MaxImageWidth = 2400;

    private readonly Action _close;
    private byte[]? _data;
    private int _pageCount;

    public AttachmentPreviewViewModel(AttachmentViewModel attachment, Action close)
    {
        Attachment = attachment;
        _close = close;
    }

    public AttachmentViewModel Attachment { get; }

    public ObservableCollection<Bitmap> Pages { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Why there is no (complete) preview.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MorePagesCommand))]
    public partial bool HasMorePages { get; set; }

    [ObservableProperty]
    public partial string? PageInfo { get; set; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _data = await Attachment.ReadAsync();
            if (_data is null)
            {
                Problem = T("Der Anhang ist zu gross für die Vorschau. Bitte öffnen oder speichern.");
            }
            else if (Attachment.PreviewKind == AttachmentPreviewKind.Image)
            {
                var data = _data;
                var image = await Task.Run(() => DecodeImage(data));
                if (image is null)
                {
                    Problem = T("Das Bild kann nicht angezeigt werden. Bitte öffnen oder speichern.");
                }
                else
                {
                    Pages.Add(image);
                }
            }
            else
            {
                await RenderPagesAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasMorePages))]
    private async Task MorePagesAsync()
    {
        IsBusy = true;
        try
        {
            await RenderPagesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => _close();

    private async Task RenderPagesAsync()
    {
        if (_data is not { } data)
        {
            return;
        }

        var first = Pages.Count;
        try
        {
            var rendering = await Task.Run(() => PdfPages.Render(data, first, PageBatch, PdfWidth));
            _pageCount = rendering.PageCount;
            foreach (var page in rendering.Pages)
            {
                Pages.Add(ToBitmap(page));
            }
        }
        catch (PdfPreviewException ex)
        {
            Problem = ex.Problem switch
            {
                PdfPreviewProblem.Password => T("Das PDF ist mit einem Passwort geschützt. Bitte öffnen oder speichern."),
                PdfPreviewProblem.Unavailable => T("Die PDF-Vorschau ist auf diesem System nicht verfügbar. Bitte öffnen oder speichern."),
                _ => T("Das PDF kann nicht angezeigt werden. Bitte öffnen oder speichern."),
            };
        }

        HasMorePages = Pages.Count < _pageCount;
        PageInfo = _pageCount == 0 ? null
            : HasMorePages ? F("Seiten 1–{0} von {1}", Pages.Count, _pageCount)
            : _pageCount == 1 ? T("1 Seite") : F("{0} Seiten", _pageCount);
    }

    private static WriteableBitmap ToBitmap(PdfPageImage page)
    {
        var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var target = bitmap.Lock();
        for (var y = 0; y < page.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(page.Pixels, y * page.Stride, target.Address + (y * target.RowBytes), page.Width * 4);
        }

        return bitmap;
    }

    private static Bitmap? DecodeImage(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var image = new Bitmap(stream);
            if (image.PixelSize.Width <= MaxImageWidth)
            {
                return image;
            }

            // Camera pictures: full resolution only costs memory here.
            image.Dispose();
            stream.Position = 0;
            return Bitmap.DecodeToWidth(stream, MaxImageWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
