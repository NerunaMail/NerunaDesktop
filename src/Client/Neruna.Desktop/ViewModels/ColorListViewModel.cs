using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// A list of names with a colour, edited the same way everywhere (tags of mails, categories of events): colour by
/// click, name saved when the field is left, ↑ to move up, a new one at the bottom with the next colour.
/// </summary>
internal abstract partial class ColorListViewModel : ViewModelBase
{
    public ObservableCollection<ColorListRow> Rows { get; } = [];

    /// <summary>Hint in the field for a new one ("Neues Schlagwort").</summary>
    public abstract string NewPlaceholder { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The colour a new row gets: the next of the choices.</summary>
    protected string NextColor => CalendarViewModel.ColorChoices[Rows.Count % CalendarViewModel.ColorChoices.Count];

    /// <summary>Saves the rows as listed.</summary>
    public abstract Task SaveAsync();

    /// <summary>A new row for <paramref name="name"/>, or the error text why there is none.</summary>
    protected abstract Task<(ColorListRow? Row, string? Error)> CreateAsync(string name);

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var (row, error) = await CreateAsync(NewName.Trim());
        if (row is null)
        {
            Error = error;
            return;
        }

        Rows.Add(row);
        NewName = string.Empty;
        await SaveAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(ColorListRow row)
    {
        Rows.Remove(row);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task MoveUpAsync(ColorListRow row)
    {
        var index = Rows.IndexOf(row);
        if (index > 0)
        {
            Rows.Move(index, index - 1);
            await SaveAsync();
        }
    }
}

/// <summary>A row of a <see cref="ColorListViewModel"/>: a colour change is saved at once, the name when its field is left.</summary>
internal partial class ColorListRow(ColorListViewModel owner, string name, string color) : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial string Color { get; set; } = color;

    partial void OnColorChanged(string value) => _ = owner.SaveAsync();

    /// <summary>Saved when the name field is left (not with every letter).</summary>
    public Task CommitNameAsync() => owner.SaveAsync();
}
