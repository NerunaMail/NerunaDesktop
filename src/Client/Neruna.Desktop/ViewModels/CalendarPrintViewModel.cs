using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using Neruna.Desktop.Infrastructure;
using SkiaSharp;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A calendar in the print dialog, ticked if it is shown in the calendar now.</summary>
internal sealed partial class PrintCalendarItem(CalendarListItem item) : ObservableObject
{
    public CalendarListItem Item { get; } = item;

    public string Name => Item.Name;

    public Avalonia.Media.IBrush Brush => Item.Brush;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = item.IsVisible;
}

/// <summary>A layout to choose (Tag, Woche, Monat, Liste).</summary>
internal sealed record PrintLayoutChoice(CalendarPrintLayout Layout, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Kalender drucken": layout, period, calendars and details on the left, a preview of the pages on the right; printed
/// through a PDF in the system's viewer, or saved as PDF.
/// </summary>
internal sealed partial class CalendarPrintViewModel : ViewModelBase
{
    private readonly CalendarController _calendar;
    private readonly IFileService _files;
    private readonly IReadOnlyDictionary<string, string> _colors;
    private IReadOnlyList<PdfPage> _pages = [];
    private int _version;

    public CalendarPrintViewModel(CalendarController calendar, IFileService files, IEnumerable<CalendarListItem> calendars, DateTime weekStart, bool workWeek)
    {
        _calendar = calendar;
        _files = files;
        Calendars = [.. calendars.Where(c => c.Info.Content != CalendarContent.Tasks).Select(c => new PrintCalendarItem(c))];
        _colors = Calendars.ToDictionary(c => c.Item.Key, c => c.Item.Color);
        Layout = Layouts[1];
        From = weekStart;
        To = weekStart.AddDays(6);
        Weekend = !workWeek;
        foreach (var item in Calendars)
        {
            item.PropertyChanged += (_, _) => _ = RefreshAsync();
        }
    }

    public event EventHandler? Finished;

    public static IReadOnlyList<PrintLayoutChoice> Layouts { get; } =
    [
        new(CalendarPrintLayout.Day, T("Tag")),
        new(CalendarPrintLayout.Week, T("Woche")),
        new(CalendarPrintLayout.Month, T("Monat")),
        new(CalendarPrintLayout.List, T("Liste")),
    ];

    public ObservableCollection<PrintCalendarItem> Calendars { get; }

    [ObservableProperty]
    public partial PrintLayoutChoice Layout { get; set; }

    [ObservableProperty]
    public partial DateTime From { get; set; }

    [ObservableProperty]
    public partial DateTime To { get; set; }

    /// <summary>For the date pickers.</summary>
    public DateTime? FromDate
    {
        get => From;
        set
        {
            if (value is { } date)
            {
                From = date.Date;
            }
        }
    }

    public DateTime? ToDate
    {
        get => To;
        set
        {
            if (value is { } date)
            {
                To = date.Date;
            }
        }
    }

    [ObservableProperty]
    public partial bool Weekend { get; set; }

    [ObservableProperty]
    public partial bool Places { get; set; } = true;

    public bool ShowsWeekend => Layout.Layout is CalendarPrintLayout.Week or CalendarPrintLayout.Month;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(HasPreview))]
    public partial Bitmap? Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageIndex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand), nameof(SavePdfCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasPreview => Preview is not null;

    public string PageText => _pages.Count == 0 ? string.Empty : F("Seite {0} von {1}", PageIndex + 1, _pages.Count);

    private bool Landscape => Layout.Layout is CalendarPrintLayout.Week or CalendarPrintLayout.Month;

    partial void OnLayoutChanged(PrintLayoutChoice value)
    {
        OnPropertyChanged(nameof(ShowsWeekend));
        // A sensible period for the layout, from the current start.
        To = value.Layout switch
        {
            CalendarPrintLayout.Day => From,
            CalendarPrintLayout.Week => CalendarPrint.StartOfWeek(From).AddDays(6),
            CalendarPrintLayout.Month => new DateTime(From.Year, From.Month, 1).AddMonths(1).AddDays(-1),
            _ => From.AddDays(13),
        };
        OnPropertyChanged(nameof(ToDate));
        _ = RefreshAsync();
    }

    partial void OnFromChanged(DateTime value)
    {
        if (To < value)
        {
            To = value;
        }

        OnPropertyChanged(nameof(FromDate));
        _ = RefreshAsync();
    }

    partial void OnToChanged(DateTime value)
    {
        OnPropertyChanged(nameof(ToDate));
        _ = RefreshAsync();
    }

    partial void OnWeekendChanged(bool value) => _ = RefreshAsync();

    partial void OnPlacesChanged(bool value) => _ = RefreshAsync();

    /// <summary>Lays the pages out anew (also when the dialog opens) and shows the first.</summary>
    public async Task RefreshAsync()
    {
        var version = ++_version;
        IsBusy = true;
        Error = null;
        try
        {
            var html = await BuildAsync();
            if (version != _version)
            {
                return;
            }

            foreach (var old in _pages)
            {
                old.Dispose();
            }

            _pages = [.. html.SelectMany(page => HtmlPdf.Render(page, CalendarPrint.Stylesheet, Landscape))];
            PageIndex = 0;
            ShowPage();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            if (version == _version)
            {
                IsBusy = false;
            }
        }
    }

    private async Task<IReadOnlyList<string>> BuildAsync()
    {
        var to = To < From ? From : To;
        var start = Layout.Layout switch
        {
            CalendarPrintLayout.Week => CalendarPrint.StartOfWeek(From),
            CalendarPrintLayout.Month => new DateTime(From.Year, From.Month, 1),
            _ => From.Date,
        };
        var end = Layout.Layout switch
        {
            CalendarPrintLayout.Week => CalendarPrint.StartOfWeek(to).AddDays(7),
            CalendarPrintLayout.Month => new DateTime(to.Year, to.Month, 1).AddMonths(1),
            _ => to.Date.AddDays(1),
        };

        // Month pages show the days of the neighbouring months in their first and last week too.
        var events = await _calendar.GetOccurrencesAsync(Calendars.Where(c => c.IsChecked).Select(c => c.Item.Info),
            new DateTimeOffset(CalendarPrint.StartOfWeek(start)), new DateTimeOffset(end.AddDays(7)));
        return CalendarPrint.Pages(Layout.Layout, From, to, events, _colors, Weekend, Places);
    }

    private void ShowPage()
    {
        var old = Preview;
        if (_pages.Count == 0)
        {
            Preview = null;
        }
        else
        {
            using var image = SKImage.FromBitmap(_pages[PageIndex].Bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var stream = data.AsStream();
            Preview = new Bitmap(stream);
        }

        old?.Dispose();
        OnPropertyChanged(nameof(PageText));
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void PreviousPage()
    {
        PageIndex--;
        ShowPage();
    }

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void NextPage()
    {
        PageIndex++;
        ShowPage();
    }

    private bool HasPrevious() => PageIndex > 0;

    private bool HasNext() => PageIndex < _pages.Count - 1;

    private string FileName => QuickActionServiceName(F("Kalender {0}", From.ToString("d", System.Globalization.CultureInfo.CurrentCulture)));

    private static string QuickActionServiceName(string name) => Neruna.Core.Mail.QuickActionService.SafeFileName(name) + ".pdf";

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task PrintAsync()
    {
        try
        {
            var pdf = HtmlPdf.ToPdf(_pages, Landscape, T("Kalender"));
            await _files.OpenAsync(FileName, stream => stream.WriteAsync(pdf).AsTask());
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Error = F("Drucken nicht möglich: {0}", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SavePdfAsync()
    {
        try
        {
            if (await _files.SaveFileAsync(T("Kalender als PDF speichern"), FileName) is { } stream)
            {
                await using (stream)
                {
                    await stream.WriteAsync(HtmlPdf.ToPdf(_pages, Landscape, T("Kalender")));
                }

                Finished?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = F("Speichern nicht möglich: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, EventArgs.Empty);

    private bool IsIdle() => !IsBusy && _pages.Count > 0;

    /// <summary>The pages are only needed while the dialog is open.</summary>
    public void Release()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        _pages = [];
        Preview?.Dispose();
    }
}
