using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Kalender → Kategorien: name and colour of the categories for events, kept apart from the tags of
/// mails. The server stores only the name, so renaming one leaves the events that have the old name without colour.
/// </summary>
internal sealed class CalendarCategoriesViewModel(CalendarCategoryService categories) : ColorListViewModel
{
    public override string NewPlaceholder => T("Neue Kategorie");

    public async Task ReloadAsync()
    {
        Rows.Clear();
        foreach (var category in await categories.GetAllAsync())
        {
            Rows.Add(new ColorListRow(this, category.Name, category.Color));
        }
    }

    protected override Task<(ColorListRow? Row, string? Error)> CreateAsync(string name) =>
        Task.FromResult<(ColorListRow?, string?)>(Rows.Any(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ? (null, F("Eine Kategorie «{0}» gibt es schon.", name))
            : (new ColorListRow(this, name, NextColor), null));

    /// <summary>Saved as listed; a row without a name is left out (it would match nothing).</summary>
    public override async Task SaveAsync()
    {
        Error = null;
        await categories.SaveAsync([.. Rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => new CalendarCategory(r.Name.Trim(), r.Color))
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
    }
}
