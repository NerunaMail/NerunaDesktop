using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Tags ("Schlagwörter") on mails: the submenu of a right click, and "Verwalten" into the settings.</summary>
internal sealed partial class MailViewModel
{
    /// <summary>"Verwalten" in the tag menu: Einstellungen → Schlagwörter.</summary>
    public event EventHandler? ManageTagsRequested;

    /// <summary>At start: the tags to show; the list follows changes (settings, Neruna Cloud).</summary>
    public async Task LoadTagsAsync()
    {
        tagService.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await RefreshTagsAsync());
        await RefreshTagsAsync();
    }

    private async Task RefreshTagsAsync()
    {
        MessageItemViewModel.KnownTags = await tagService.GetAllAsync();
        foreach (var message in _allMessages)
        {
            message.RefreshTags();
        }
    }

    /// <summary>For the right-click menu: every tag, ticked when all selected mails have it; whether their accounts keep tags.</summary>
    internal async Task<(IReadOnlyList<(MailTag Tag, bool IsSet)> Tags, bool Supported)> TagMenuAsync()
    {
        var targets = ActionTargets;
        if (targets.Count == 0)
        {
            return ([], false);
        }

        var supported = true;
        foreach (var connection in targets.Select(FolderOf).OfType<MailFolderNode>().Select(f => f.Account.Connection).DistinctBy(c => c.Id))
        {
            supported &= await mail.SupportsTagsAsync(connection);
        }

        return ([.. MessageItemViewModel.KnownTags.Select(t => (t, targets.All(m => m.Has(t))))], supported);
    }

    /// <summary>Puts the tag on the selected mails, or takes it off when all of them have it already.</summary>
    [RelayCommand]
    private async Task ToggleTagAsync(MailTag tag)
    {
        var targets = ActionTargets;
        if (targets.Count == 0)
        {
            return;
        }

        var add = !targets.All(m => m.Has(tag));
        if (await RunOnMessagesAsync(targets, T("Schlagwort setzen"), (connection, folder, ids) => mail.SetTagAsync(connection, folder, ids, tag, add), removesMessages: false))
        {
            foreach (var message in targets)
            {
                message.Keywords = [.. message.Keywords.Where(k => !tag.Matches(k)), .. add ? [tag.Keyword] : Array.Empty<string>()];
            }
        }
    }

    [RelayCommand]
    private void ManageTags() => ManageTagsRequested?.Invoke(this, EventArgs.Empty);
}
