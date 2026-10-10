using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// The folder tree of the mail page: one node per mail account with its folders, "Favoriten" on top, what is collapsed
/// (kept across restarts), the order of the accounts, and accounts still being set up. Choosing a folder raises
/// <see cref="FolderSelected"/>; the mail page opens it.
/// </summary>
internal sealed partial class MailFolderTreeViewModel(IAccountStore accounts, MailController mail, ISettingsStore settings) : ViewModelBase
{
    /// <summary>A folder was chosen in the tree (also through its favourite).</summary>
    public event EventHandler<MailFolderNode>? FolderSelected;

    /// <summary>The user moved an account in the folder tree; other lists (settings) follow.</summary>
    public event EventHandler? AccountsReordered;

    /// <summary>Raised for "Abwesenheitsnotiz …" on an account (the shell shows the dialog).</summary>
    public event EventHandler<MailAccountNode>? AutoReplyRequested;

    /// <summary>Raised for "Ordner abonnieren …" on an account (the shell shows the dialog).</summary>
    public event EventHandler<MailAccountNode>? FolderSubscriptionsRequested;

    public ObservableCollection<MailAccountNode> Accounts { get; } = [];

    /// <summary>What the folder tree shows: "Favoriten" (when there are some), then the accounts.</summary>
    public ObservableCollection<object> TreeRoots { get; } = [];

    public FavoritesNode Favorites { get; } = new();

    public bool HasMailAccounts => Accounts.Count > 0;

    [ObservableProperty]
    public partial object? SelectedTreeItem { get; set; }

    private List<string>? _favoriteKeys;
    private bool _treeRootsFollow;

    /// <summary>Builds the tree from the stored folders of every mail account.</summary>
    public async Task LoadAsync()
    {
        if (!_treeRootsFollow)
        {
            // The tree's top level follows the accounts: moved in place, otherwise built again.
            _treeRootsFollow = true;
            Accounts.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
                {
                    var offset = TreeRoots.Count > 0 && TreeRoots[0] is FavoritesNode ? 1 : 0;
                    TreeRoots.Move(e.OldStartingIndex + offset, e.NewStartingIndex + offset);
                }
                else
                {
                    RebuildTreeRoots();
                }
            };
        }

        Accounts.Clear();
        _collapsed ??= await LoadCollapsedAsync();
        _favoriteKeys ??= await LoadFavoritesAsync();
        foreach (var account in await accounts.GetAccountsAsync())
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                var folders = await mail.GetFoldersAsync(connection.Id);
                var node = new MailAccountNode(account, connection, BuildTree(folders)) { IsSettingUp = _settingUp.Contains(connection.Id) };
                TrackExpansion(node, node.ExpansionKey);
                foreach (var folder in node.AllFolders())
                {
                    TrackExpansion(folder, folder.ExpansionKey);
                }

                Accounts.Add(node);
            }
        }

        OnPropertyChanged(nameof(HasMailAccounts));
        RebuildFavorites();
    }

    /// <summary>After a sync: the unread counts, if no folder came or went.</summary>
    /// <returns>False when folders or accounts changed – then the tree has to be loaded again.</returns>
    public async Task<bool> UpdateCountsAsync()
    {
        var structure = new List<(MailAccountNode? Node, IReadOnlyList<MailFolder> Folders)>();
        var index = 0;
        foreach (var account in await accounts.GetAccountsAsync())
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                var node = index < Accounts.Count && Accounts[index].Connection.Id == connection.Id && Accounts[index].Title == MailAccountNode.TitleOf(account) ? Accounts[index] : null;
                structure.Add((node, await mail.GetFoldersAsync(connection.Id)));
                index++;
            }
        }

        var unchanged = structure.Count == Accounts.Count && structure.All(s => s.Node is { } node
            && node.AllFolders().Select(f => f.Folder.RemoteId).Order(StringComparer.Ordinal)
                .SequenceEqual(s.Folders.Select(f => f.RemoteId).Order(StringComparer.Ordinal)));
        if (!unchanged)
        {
            return false;
        }

        foreach (var (node, folders) in structure)
        {
            var byId = folders.ToDictionary(f => f.RemoteId, StringComparer.Ordinal);
            foreach (var folder in node!.AllFolders())
            {
                folder.UnreadCount = byId[folder.Folder.RemoteId].UnreadCount;
            }
        }

        return true;
    }

    partial void OnSelectedTreeItemChanged(object? value)
    {
        // A favourite opens its folder (the list and everything else work with the real folder).
        if (value is FavoriteFolderNode favorite)
        {
            value = favorite.Target;
        }

        if (value is MailFolderNode folder)
        {
            FolderSelected?.Invoke(this, folder);
        }
    }

    // Folder tree: what the user collapsed stays collapsed after a restart (new folders start expanded).
    private HashSet<string>? _collapsed;

    private Task<HashSet<string>> LoadCollapsedAsync() => settings.GetSetAsync(SettingKeys.CollapsedFolders);

    private void TrackExpansion(ObservableObject node, string key)
    {
        var collapsed = _collapsed!;
        switch (node)
        {
            case MailAccountNode account:
                account.IsExpanded = !collapsed.Contains(key);
                break;
            case MailFolderNode folder:
                folder.IsExpanded = !collapsed.Contains(key);
                break;
        }

        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MailFolderNode.IsExpanded))
            {
                return;
            }

            var expanded = node is MailAccountNode a ? a.IsExpanded : ((MailFolderNode)node).IsExpanded;
            if (expanded ? collapsed.Remove(key) : collapsed.Add(key))
            {
                _ = settings.SetListAsync(SettingKeys.CollapsedFolders, collapsed);
            }
        };
    }

    [RelayCommand]
    private Task MoveAccountUpAsync(MailAccountNode node) => MoveAccountAsync(node, -1);

    [RelayCommand]
    private Task MoveAccountDownAsync(MailAccountNode node) => MoveAccountAsync(node, +1);

    [RelayCommand]
    private void SubscribeFolders(MailAccountNode node) => FolderSubscriptionsRequested?.Invoke(this, node);

    [RelayCommand]
    private void AutoReply(MailAccountNode node) => AutoReplyRequested?.Invoke(this, node);

    [RelayCommand]
    private async Task AddFavoriteAsync(MailFolderNode folder)
    {
        _favoriteKeys ??= await LoadFavoritesAsync();
        if (!_favoriteKeys.Contains(folder.ExpansionKey))
        {
            _favoriteKeys.Add(folder.ExpansionKey);
            await SaveFavoritesAsync();
        }
    }

    /// <summary>From the folder itself or from its entry under "Favoriten".</summary>
    [RelayCommand]
    private async Task RemoveFavoriteAsync(object node)
    {
        var key = node switch
        {
            FavoriteFolderNode favorite => favorite.Target.ExpansionKey,
            MailFolderNode folder => folder.ExpansionKey,
            _ => null,
        };
        if (key is not null && _favoriteKeys?.Remove(key) == true)
        {
            await SaveFavoritesAsync();
        }
    }

    [RelayCommand]
    private Task MoveFavoriteUpAsync(FavoriteFolderNode node) => MoveFavoriteAsync(node, -1);

    [RelayCommand]
    private Task MoveFavoriteDownAsync(FavoriteFolderNode node) => MoveFavoriteAsync(node, +1);

    private async Task MoveFavoriteAsync(FavoriteFolderNode node, int delta)
    {
        var index = _favoriteKeys?.IndexOf(node.Target.ExpansionKey) ?? -1;
        if (index < 0 || index + delta < 0 || index + delta >= _favoriteKeys!.Count)
        {
            return;
        }

        (_favoriteKeys[index], _favoriteKeys[index + delta]) = (_favoriteKeys[index + delta], _favoriteKeys[index]);
        await SaveFavoritesAsync();
    }

    private async Task SaveFavoritesAsync()
    {
        await settings.SetListAsync(SettingKeys.MailFavorites, _favoriteKeys ?? []);
        RebuildFavorites();
    }

    private async Task<List<string>> LoadFavoritesAsync() => await settings.GetListAsync(SettingKeys.MailFavorites) ?? [];

    // Favourites whose folder is gone (hidden, account removed) are skipped but kept: they come back with the folder.
    private void RebuildFavorites()
    {
        var folders = Accounts.SelectMany(a => a.AllFolders()).ToDictionary(f => f.ExpansionKey, StringComparer.Ordinal);
        foreach (var folder in folders.Values)
        {
            folder.IsFavorite = _favoriteKeys?.Contains(folder.ExpansionKey) == true;
        }

        Favorites.Items.Clear();
        foreach (var key in _favoriteKeys ?? [])
        {
            if (folders.TryGetValue(key, out var folder))
            {
                Favorites.Items.Add(new FavoriteFolderNode(folder));
            }
        }

        var shown = TreeRoots.Count > 0 && TreeRoots[0] is FavoritesNode;
        if (Favorites.Items.Count > 0 && !shown)
        {
            TreeRoots.Insert(0, Favorites);
        }
        else if (Favorites.Items.Count == 0 && shown)
        {
            TreeRoots.RemoveAt(0);
        }
    }

    private void RebuildTreeRoots()
    {
        var selected = SelectedTreeItem;
        TreeRoots.Clear();
        if (Favorites.Items.Count > 0)
        {
            TreeRoots.Add(Favorites);
        }

        foreach (var account in Accounts)
        {
            TreeRoots.Add(account);
        }

        SelectedTreeItem = selected;
    }

    // Same order as in Einstellungen → Konten; the tree is rearranged in place (expanded folders stay open).
    private async Task MoveAccountAsync(MailAccountNode node, int delta)
    {
        var index = Accounts.IndexOf(node);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Accounts.Count)
        {
            return;
        }

        Accounts.Move(index, target);
        await accounts.SetOrderAsync(Accounts.Select(a => a.Account.Id).Distinct().ToList());
        AccountsReordered?.Invoke(this, EventArgs.Empty);
    }

    // Accounts just added whose first sync is still running: shown at once, with "Ordner werden geladen …".
    private readonly HashSet<Guid> _settingUp = [];

    public void MarkSettingUp(Guid connectionId, bool settingUp)
    {
        if (settingUp ? _settingUp.Add(connectionId) : _settingUp.Remove(connectionId))
        {
            foreach (var node in Accounts.Where(a => a.Connection.Id == connectionId))
            {
                node.IsSettingUp = settingUp;
            }
        }
    }

    private static List<MailFolderNode> BuildTree(IReadOnlyList<MailFolder> folders)
    {
        var nodes = folders.ToDictionary(f => f.RemoteId, f => new MailFolderNode(f), StringComparer.Ordinal);
        var roots = new List<MailFolderNode>();
        foreach (var node in folders.Select(f => nodes[f.RemoteId]))
        {
            if (node.Folder.ParentRemoteId is { } parent && nodes.TryGetValue(parent, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return roots;
    }
}
