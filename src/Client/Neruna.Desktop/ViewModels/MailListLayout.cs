using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;

namespace Neruna.Desktop.ViewModels;

/// <summary>What the mail list is sorted by.</summary>
internal enum MailSort
{
    Date,
    From,
    Subject,
    Size,
}

/// <summary>
/// How the mail list looks: as cards or as a table (one line per mail, in columns), the table's column widths and the
/// sorting. Kept on this computer ("ui.layout…"), so it is the same after a restart and in every folder.
/// </summary>
internal sealed partial class MailListLayout(ISettingsStore settings) : ObservableObject
{
    private const string Key = "ui.layout.mailList";

    private bool _loading;

    private sealed record Stored(bool Table, double From, double Date, double Size, MailSort Sort, bool Descending);

    [ObservableProperty]
    public partial bool IsTable { get; set; }

    /// <summary>Width of the "Von" column (the subject takes what is left).</summary>
    [ObservableProperty]
    public partial double FromWidth { get; set; } = 160;

    [ObservableProperty]
    public partial double DateWidth { get; set; } = 115;

    [ObservableProperty]
    public partial double SizeWidth { get; set; } = 60;

    [ObservableProperty]
    public partial MailSort Sort { get; set; } = MailSort.Date;

    [ObservableProperty]
    public partial bool Descending { get; set; } = true;

    // ▼/▲ behind the column the table is sorted by.
    public string DateArrow => Arrow(MailSort.Date);

    public string FromArrow => Arrow(MailSort.From);

    public string SubjectArrow => Arrow(MailSort.Subject);

    public string SizeArrow => Arrow(MailSort.Size);

    public bool IsByDate => Sort == MailSort.Date;

    public bool IsByFrom => Sort == MailSort.From;

    public bool IsBySubject => Sort == MailSort.Subject;

    public bool IsBySize => Sort == MailSort.Size;

    private string Arrow(MailSort column) => Sort != column ? string.Empty : Descending ? " ▼" : " ▲";

    /// <summary>Sorting changed: the list is ordered anew.</summary>
    public event EventHandler? SortChanged;

    public async Task LoadAsync()
    {
        try
        {
            if (await settings.GetAsync(Key) is { } json && JsonSerializer.Deserialize<Stored>(json) is { } stored)
            {
                _loading = true;
                IsTable = stored.Table;
                FromWidth = Clamp(stored.From, 160);
                DateWidth = Clamp(stored.Date, 115);
                SizeWidth = Clamp(stored.Size, 60);
                Sort = stored.Sort;
                Descending = stored.Descending;
            }
        }
        catch (JsonException)
        {
            // an older or damaged value: the defaults
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Sorts by <paramref name="sort"/>; the same column again turns the order around.</summary>
    public void SortBy(MailSort sort)
    {
        if (Sort == sort)
        {
            Descending = !Descending;
        }
        else
        {
            _loading = true;
            Sort = sort;
            _loading = false;
            Descending = sort is MailSort.Date or MailSort.Size; // newest and largest first; names A–Z
        }
    }

    /// <summary>After dragging a column border in the table's header.</summary>
    public void SetWidths(double from, double date, double size)
    {
        _loading = true;
        (FromWidth, DateWidth, SizeWidth) = (Clamp(from, FromWidth), Clamp(date, DateWidth), Clamp(size, SizeWidth));
        _loading = false;
        _ = SaveAsync();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Sort) or nameof(Descending))
        {
            foreach (var name in new[] { nameof(DateArrow), nameof(FromArrow), nameof(SubjectArrow), nameof(SizeArrow), nameof(IsByDate), nameof(IsByFrom), nameof(IsBySubject), nameof(IsBySize) })
            {
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }

        if (_loading)
        {
            return;
        }

        if (e.PropertyName is nameof(Sort) or nameof(Descending))
        {
            SortChanged?.Invoke(this, EventArgs.Empty);
        }

        _ = SaveAsync();
    }

    private Task SaveAsync() =>
        settings.SetAsync(Key, JsonSerializer.Serialize(new Stored(IsTable, FromWidth, DateWidth, SizeWidth, Sort, Descending)));

    private static double Clamp(double width, double fallback) => double.IsFinite(width) && width >= 40 ? Math.Min(width, 800) : fallback;
}
