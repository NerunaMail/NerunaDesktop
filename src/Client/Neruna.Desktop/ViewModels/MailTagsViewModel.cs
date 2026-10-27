using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Settings → Schlagwörter: the own tags (name and colour, a new one at the bottom) and, read-only, those of the
/// organisation from Neruna Cloud. Renaming keeps the keyword, so mails tagged before stay tagged.
/// </summary>
internal sealed partial class MailTagsViewModel(MailTagService tags) : ViewModelBase
{
    public ObservableCollection<MailTagRow> Own { get; } = [];

    public ObservableCollection<MailTag> FromCloud { get; } = [];

    public bool HasCloud => FromCloud.Count > 0;

    public static IReadOnlyList<string> ColorChoices => CalendarViewModel.ColorChoices;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public async Task ReloadAsync()
    {
        var all = await tags.GetAllAsync();
        FromCloud.Clear();
        foreach (var tag in all.Where(t => t.IsFromCloud))
        {
            FromCloud.Add(tag);
        }

        Own.Clear();
        foreach (var tag in all.Where(t => !t.IsFromCloud))
        {
            Own.Add(new MailTagRow(this, tag));
        }

        OnPropertyChanged(nameof(HasCloud));
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        var all = await tags.GetAllAsync();
        if (all.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = F("Ein Schlagwort «{0}» gibt es schon.", name);
            return;
        }

        var color = ColorChoices[Own.Count % ColorChoices.Count];
        Own.Add(new MailTagRow(this, new MailTag(MailTag.KeywordFor(name, all.Select(t => t.Keyword)), name, color)));
        NewName = string.Empty;
        Error = null;
        await SaveAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(MailTagRow row)
    {
        Own.Remove(row);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task MoveUpAsync(MailTagRow row)
    {
        var index = Own.IndexOf(row);
        if (index > 0)
        {
            Own.Move(index, index - 1);
            await SaveAsync();
        }
    }

    internal async Task SaveAsync()
    {
        Error = null;
        await tags.SaveOwnAsync([.. Own.Select(r => r.ToTag())]);
    }
}

/// <summary>An own tag in the settings: name and colour can be changed, the keyword stays.</summary>
internal sealed partial class MailTagRow(MailTagsViewModel owner, MailTag tag) : ObservableObject
{
    public string Keyword { get; } = tag.Keyword;

    [ObservableProperty]
    public partial string Name { get; set; } = tag.Name;

    [ObservableProperty]
    public partial string Color { get; set; } = tag.Color;

    public MailTag ToTag() => new(Keyword, string.IsNullOrWhiteSpace(Name) ? Keyword : Name.Trim(), Color);

    partial void OnColorChanged(string value) => _ = owner.SaveAsync();

    /// <summary>Saved when the name field is left (not with every letter).</summary>
    public Task CommitNameAsync() => owner.SaveAsync();
}
