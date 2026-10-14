using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>What the PDF preview needs from the mail page for stamping: the stamps, the name, answering with the PDF.</summary>
internal interface IAttachmentActions
{
    Task<IReadOnlyList<PdfStamp>> GetStampsAsync();

    /// <summary>The name on the stamp: the one set in the settings, else the person logged in to the computer.</summary>
    Task<string?> GetStampNameAsync();

    /// <summary>Answer or forward the open mail with the (stamped) attachment.</summary>
    Task RespondWithAsync(AttachmentViewModel attachment, byte[] content, QuickActionStepKind kind);
}

/// <summary>One shown page (or the image); the picture changes while a stamp is being placed.</summary>
internal sealed partial class PreviewPage(int index, Bitmap image) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    public partial Bitmap Image { get; set; } = image;
}

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
    private readonly IAttachmentActions? _actions;
    private byte[]? _data;
    private int _pageCount;

    /// <param name="content">Already changed content (stamped by an earlier step of a quick action) instead of the attachment's.</param>
    public AttachmentPreviewViewModel(AttachmentViewModel attachment, Action close, IAttachmentActions? actions = null, byte[]? content = null)
    {
        Attachment = attachment;
        _close = close;
        _actions = actions;
        _data = content;
        IsStamped = content is not null;
    }

    public AttachmentViewModel Attachment { get; }

    public ObservableCollection<PreviewPage> Pages { get; } = [];

    // ── Stamping: a click on a page writes the stamp into a copy of the PDF and shows that page anew (as it will be
    //    saved); "Übernehmen" keeps it, another click moves it. ──

    /// <summary>The stamps to choose from ("Stempeln" menu).</summary>
    public ObservableCollection<PdfStamp> Stamps { get; } = [];

    /// <summary>A PDF that could be read, on the mail page: it can be stamped.</summary>
    public bool CanStamp => _actions is not null && Attachment.PreviewKind == AttachmentPreviewKind.Pdf && Problem is null && Pages.Count > 0;

    /// <summary>The stamp being placed (the bar with the hint shows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStamping), nameof(StampHint))]
    public partial PdfStamp? ActiveStamp { get; set; }

    public bool IsStamping => ActiveStamp is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StampHint))]
    [NotifyCanExecuteChangedFor(nameof(ApplyStampCommand))]
    public partial bool HasPlacedStamp { get; set; }

    public string? StampHint => ActiveStamp is not { } stamp ? null
        : HasPlacedStamp ? F("Stempel «{0}» gesetzt – ein Klick an eine andere Stelle verschiebt ihn.", stamp.Text)
        : F("Klicken Sie auf die Stelle, an die der Stempel «{0}» kommt.", stamp.Text);

    /// <summary>Stamped (and kept): saving, answering and forwarding use the stamped PDF.</summary>
    [ObservableProperty]
    public partial bool IsStamped { get; set; }

    /// <summary>Opened by a quick action: the buttons say "Weiter" and "Abbrechen".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyLabel), nameof(CancelLabel))]
    public partial bool IsForAction { get; set; }

    public string ApplyLabel => IsForAction ? T("Weiter") : T("Übernehmen");

    public string CancelLabel => IsForAction ? T("Aktion abbrechen") : T("Verwerfen");

    private byte[]? _placed;
    private (PreviewPage Page, Bitmap Original)? _placedOn;
    private TaskCompletionSource<byte[]?>? _forAction;

    [RelayCommand]
    private void StartStamp(PdfStamp? stamp)
    {
        if (stamp is null || !CanStamp)
        {
            return;
        }

        RestorePlaced();
        ActiveStamp = stamp;
    }

    /// <summary>A click on a page while stamping (position as share of the page's width and height, from top left).</summary>
    public async Task PlaceAsync(PreviewPage page, double x, double y)
    {
        if (ActiveStamp is not { } stamp || _data is not { } data)
        {
            return;
        }

        var name = _actions is null ? null : await _actions.GetStampNameAsync();
        var mark = new PdfStampMark(page.Index, x, y, stamp.Text, stamp.Lines(name, DateTime.Now), stamp.Color);
        try
        {
            var (stamped, image) = await Task.Run(() =>
            {
                var result = PdfPages.Stamp(data, mark);
                return (result, PdfPages.Render(result, page.Index, 1, PdfWidth).Pages[0]);
            });
            if (ActiveStamp != stamp)
            {
                return; // cancelled meanwhile
            }

            RestorePlaced();
            _placedOn = (page, page.Image);
            page.Image = ToBitmap(image);
            _placed = stamped;
            HasPlacedStamp = true;
        }
        catch (PdfPreviewException)
        {
            Problem = T("In dieses PDF kann kein Stempel gesetzt werden.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPlacedStamp))]
    private void ApplyStamp()
    {
        if (_placed is null)
        {
            return;
        }

        _data = _placed;
        _placed = null;
        _placedOn = null; // the page keeps the stamped picture
        HasPlacedStamp = false;
        ActiveStamp = null;
        IsStamped = true;
        _forAction?.TrySetResult(_data);
        _forAction = null;
    }

    [RelayCommand]
    private void CancelStamp()
    {
        RestorePlaced();
        ActiveStamp = null;
        _forAction?.TrySetResult(null);
        _forAction = null;
    }

    private void RestorePlaced()
    {
        if (_placedOn is { } placed)
        {
            placed.Page.Image = placed.Original;
        }

        _placedOn = null;
        _placed = null;
        HasPlacedStamp = false;
    }

    /// <summary>A quick action's step: the stamp is ready to place; the stamped PDF, or null when the action is cancelled.</summary>
    public Task<byte[]?> StampForActionAsync(PdfStamp stamp)
    {
        if (!CanStamp)
        {
            return Task.FromResult<byte[]?>(null);
        }

        IsForAction = true;
        _forAction = new TaskCompletionSource<byte[]?>();
        StartStamp(stamp);
        return _forAction.Task;
    }

    /// <summary>"Speichern": the stamped PDF, else the attachment as it came.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsStamped && _data is { } data)
        {
            await Attachment.SaveContentAsync(data);
        }
        else
        {
            await Attachment.SaveCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private Task ReplyWithAsync() => RespondAsync(QuickActionStepKind.Reply);

    [RelayCommand]
    private Task ForwardWithAsync() => RespondAsync(QuickActionStepKind.Forward);

    private Task RespondAsync(QuickActionStepKind kind) =>
        _actions is not null && _data is { } data ? _actions.RespondWithAsync(Attachment, data, kind) : Task.CompletedTask;

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
            _data ??= await Attachment.ReadAsync();
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
                    Pages.Add(new PreviewPage(0, image));
                }
            }
            else
            {
                await RenderPagesAsync();
                if (_actions is not null)
                {
                    foreach (var stamp in await _actions.GetStampsAsync())
                    {
                        Stamps.Add(stamp);
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanStamp));
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
    private void Close()
    {
        CancelStamp();
        _close();
    }

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
                Pages.Add(new PreviewPage(Pages.Count, ToBitmap(page)));
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
