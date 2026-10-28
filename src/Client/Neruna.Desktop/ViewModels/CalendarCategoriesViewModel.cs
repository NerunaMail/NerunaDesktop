using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Kalender → Kategorien: name and colour of the categories for events, kept apart from the tags of
/// mails. The server stores only the name, so renaming one leaves the events that have the old name without colour.
/// </summary>
internal sealed partial class CalendarCategoriesViewModel(CalendarCategoryService categories) : ViewModelBase
{
    public ObservableCollection<CalendarCategoryRow> Items { get; } = [];

    public static IReadOnlyList<string> ColorChoices => CalendarViewModel.ColorChoices;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public async Task ReloadAsync()
    {
        Items.Clear();
        foreach (var category in await categories.GetAllAsync())
        {
            Items.Add(new CalendarCategoryRow(this, category));
        }
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        if (Items.Any(i => string.Equals(i.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = F("Eine Kategorie «{0}» gibt es schon.", name);
            return;
        }

        Items.Add(new CalendarCategoryRow(this, new CalendarCategory(name, ColorChoices[Items.Count % ColorChoices.Count])));
        NewName = string.Empty;
        await SaveAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(CalendarCategoryRow row)
    {
        Items.Remove(row);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task MoveUpAsync(CalendarCategoryRow row)
    {
        var index = Items.IndexOf(row);
        if (index > 0)
        {
            Items.Move(index, index - 1);
            await SaveAsync();
        }
    }

    /// <summary>Saved as listed; a row without a name is left out (it would match nothing).</summary>
    internal async Task SaveAsync()
    {
        Error = null;
        await categories.SaveAsync([.. Items.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.ToCategory())
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
    }
}

internal sealed partial class CalendarCategoryRow(CalendarCategoriesViewModel owner, CalendarCategory category) : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = category.Name;

    [ObservableProperty]
    public partial string Color { get; set; } = category.Color;

    public CalendarCategory ToCategory() => new(Name.Trim(), Color);

    partial void OnColorChanged(string value) => _ = owner.SaveAsync();

    /// <summary>Saved when the name field is left (not with every letter).</summary>
    public Task CommitNameAsync() => owner.SaveAsync();
}
