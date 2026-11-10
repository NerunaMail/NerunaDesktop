using System.Collections.ObjectModel;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Kalender → Kategorien: name and colour of the categories for events, kept apart from the tags of
/// mails. The server stores only the name, so renaming one leaves the events that have the old name without colour.
/// Accounts that store colours on their server (Microsoft 365, Exchange) also get the changed colours in their list.
/// The organisation's categories (Neruna Cloud) are shown above, not editable.
/// </summary>
internal sealed class CalendarCategoriesViewModel(CalendarCategoryService categories, CalendarController calendar) : ColorListViewModel
{
    public ObservableCollection<CalendarCategory> FromCloud { get; } = [];

    public bool HasCloud => FromCloud.Count > 0;

    public override string NewPlaceholder => T("Neue Kategorie");

    public async Task ReloadAsync()
    {
        var all = await categories.GetAllAsync();
        FromCloud.Clear();
        foreach (var category in all.Where(c => c.IsFromCloud))
        {
            FromCloud.Add(category);
        }

        Rows.Clear();
        foreach (var category in all.Where(c => !c.IsFromCloud))
        {
            Rows.Add(new ColorListRow(this, category.Name, category.Color));
        }

        OnPropertyChanged(nameof(HasCloud));
    }

    protected override Task<(ColorListRow? Row, string? Error)> CreateAsync(string name) =>
        Task.FromResult<(ColorListRow?, string?)>(Rows.Any(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            || FromCloud.Any(c => c.Matches(name))
            ? (null, F("Eine Kategorie «{0}» gibt es schon.", name))
            : (new ColorListRow(this, name, NextColor), null));

    /// <summary>Saved as listed; a row without a name is left out (it would match nothing).</summary>
    public override async Task SaveAsync()
    {
        Error = null;
        var before = await categories.GetAllAsync();
        List<CalendarCategory> now = [.. Rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => new CalendarCategory(r.Name.Trim(), r.Color))
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
        await categories.SaveAsync(now);

        // Only new or recoloured ones: the server's other categories stay as they are.
        var changed = now.Where(c => !before.Any(b => b.Matches(c.Name) && string.Equals(b.Color, c.Color, StringComparison.OrdinalIgnoreCase))).ToList();
        try
        {
            if (await calendar.SetCategoryColorsOnServerAsync(changed) is { Count: > 0 } failed)
            {
                Error = T("Auf dem Server nicht gespeichert: ") + string.Join("; ", failed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Auf dem Server nicht gespeichert: ") + ex.Message;
        }
    }
}
