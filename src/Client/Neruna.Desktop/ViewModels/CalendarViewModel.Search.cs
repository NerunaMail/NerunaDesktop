using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Termine suchen": the events of the shown calendars whose title, place, description or people contain the words,
/// as a list instead of the week – coming ones first, then the past ones (newest first). A series appears once.
/// </summary>
internal sealed partial class CalendarViewModel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);
    private int _searchVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    public partial string SearchText { get; set; } = string.Empty;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public ObservableCollection<CalendarSearchGroup> SearchResults { get; } = [];

    [ObservableProperty]
    public partial string? SearchStatus { get; set; }

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    private async Task SearchAsync(string query)
    {
        var version = ++_searchVersion;
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResults.Clear();
            SearchStatus = null;
            return;
        }

        // While typing: only once the typing pauses.
        await Task.Delay(SearchDelay);
        if (version != _searchVersion)
        {
            return;
        }

        SearchStatus = T("Suche …");
        var visible = Calendars.Where(IsShown).ToList();
        var colors = visible.ToDictionary(c => (c.Info.ConnectionId, c.Info.RemoteId), c => c.Color);
        var names = visible.ToDictionary(c => (c.Info.ConnectionId, c.Info.RemoteId), c => c.Info.Name);
        var shown = visible.Select(c => c.Info).ToList();
        var now = DateTimeOffset.Now;
        var found = await Task.Run(() => calendar.SearchAsync(shown, query, now));
        var known = await categories.GetAllAsync();
        if (version != _searchVersion)
        {
            return; // A newer search took over.
        }

        CalendarSearchHit Hit(CalendarOccurrence o) => new(
            new CalendarEventItem(o, colors.GetValueOrDefault((o.Calendar.ConnectionId, o.Calendar.RemoteId)) ?? Palette[0], known),
            names.GetValueOrDefault((o.Calendar.ConnectionId, o.Calendar.RemoteId)) ?? string.Empty);

        var today = now.LocalDateTime.Date;
        var coming = found.Where(o => o.End.LocalDateTime > today || o.Start.LocalDateTime.Date >= today).Select(Hit).ToList();
        var past = found.Where(o => !(o.End.LocalDateTime > today || o.Start.LocalDateTime.Date >= today)).Reverse().Select(Hit).ToList();

        SearchResults.Clear();
        if (coming.Count > 0)
        {
            SearchResults.Add(new CalendarSearchGroup(T("Bevorstehend"), coming));
        }

        if (past.Count > 0)
        {
            SearchResults.Add(new CalendarSearchGroup(T("Vergangen"), past));
        }

        SearchStatus = found.Count == 0 ? T("Keine Termine gefunden.")
            : found.Count == 1 ? T("1 Termin gefunden")
            : F("{0} Termine gefunden", found.Count);
    }

    [RelayCommand]
    private void CloseSearch() => SearchText = string.Empty;

    /// <summary>A hit: its week comes up behind the list, and the event opens.</summary>
    [RelayCommand]
    private async Task OpenSearchHitAsync(CalendarSearchHit hit)
    {
        await SelectDayAsync(hit.Item.Occurrence.Start.LocalDateTime.Date);
        await OpenOccurrenceAsync(hit.Item.Occurrence);
    }
}

internal sealed record CalendarSearchGroup(string Title, IReadOnlyList<CalendarSearchHit> Hits);

internal sealed record CalendarSearchHit(CalendarEventItem Item, string CalendarName)
{
    public string DateText
    {
        get
        {
            var start = Item.Occurrence.Start.LocalDateTime;
            var end = Item.Occurrence.End.LocalDateTime;
            var date = start.ToString("ddd d. MMMM yyyy", CultureInfo.CurrentCulture);
            // All-day over several days: "bis" the last day (the end is the morning after).
            var lastDay = Item.Occurrence.IsAllDay ? end.Date.AddDays(-1) : end.Date;
            return lastDay > start.Date ? date + " – " + lastDay.ToString("ddd d. MMMM yyyy", CultureInfo.CurrentCulture) : date;
        }
    }
}
