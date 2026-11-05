using System.Collections.ObjectModel;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Settings → Schlagwörter: the own tags (name and colour, a new one at the bottom) and, read-only, those of the
/// organisation from Neruna Cloud. Renaming keeps the keyword, so mails tagged before stay tagged.
/// </summary>
internal sealed class MailTagsViewModel(MailTagService tags) : ColorListViewModel
{
    public ObservableCollection<MailTag> FromCloud { get; } = [];

    public bool HasCloud => FromCloud.Count > 0;

    public override string NewPlaceholder => T("Neues Schlagwort");

    public async Task ReloadAsync()
    {
        var all = await tags.GetAllAsync();
        FromCloud.Clear();
        foreach (var tag in all.Where(t => t.IsFromCloud))
        {
            FromCloud.Add(tag);
        }

        Rows.Clear();
        foreach (var tag in all.Where(t => !t.IsFromCloud))
        {
            Rows.Add(new MailTagRow(this, tag));
        }

        OnPropertyChanged(nameof(HasCloud));
    }

    protected override async Task<(ColorListRow? Row, string? Error)> CreateAsync(string name)
    {
        var all = await tags.GetAllAsync();
        if (all.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, F("Ein Schlagwort «{0}» gibt es schon.", name));
        }

        return (new MailTagRow(this, new MailTag(MailTag.KeywordFor(name, all.Select(t => t.Keyword)), name, NextColor)), null);
    }

    public override async Task SaveAsync()
    {
        Error = null;
        await tags.SaveOwnAsync([.. Rows.OfType<MailTagRow>().Select(r => r.ToTag())]);
    }
}

/// <summary>An own tag in the settings: name and colour can be changed, the keyword stays.</summary>
internal sealed class MailTagRow(MailTagsViewModel owner, MailTag tag) : ColorListRow(owner, tag.Name, tag.Color)
{
    public string Keyword { get; } = tag.Keyword;

    public MailTag ToTag() => new(Keyword, string.IsNullOrWhiteSpace(Name) ? Keyword : Name.Trim(), Color);
}
