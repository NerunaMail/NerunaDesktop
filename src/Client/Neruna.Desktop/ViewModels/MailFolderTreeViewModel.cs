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
internal sealed partial class MailFolderTreeViewModel(IAccountStore accounts, MailController mail, ISettingsStore settings, Neruna.Core.Workspaces.WorkspaceService workspaces) : ViewModelBase
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

    /// <summary>"Favoriten" on its own (fixed above the accounts, which scroll): empty while there are none.</summary>
    public ObservableCollection<object> FavoriteRoots { get; } = [];

    public bool HasFavorites => FavoriteRoots.Count > 0;

    /// <summary>The selection in the favourites' tree (the other tree's selection reads as nothing here).</summary>
    public object? SelectedFavoriteItem
    {
        get => SelectedTreeItem is FavoritesNode or FavoriteFolderNode ? SelectedTreeItem : null;
        set
        {
            if (value is not null)
            {
                SelectedTreeItem = value;
            }
        }
    }

    /// <summary>The selection in the accounts' tree.</summary>
    public object? SelectedAccountItem
    {
        get => SelectedTreeItem is FavoritesNode or FavoriteFolderNode ? null : SelectedTreeItem;
        set
        {
            if (value is not null)
            {
                SelectedTreeItem = value;
            }
        }
    }

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
        // The open workspace decides which accounts and folders show (everything keeps synchronising).
        foreach (var account in (await accounts.GetAccountsAsync()).Where(a => workspaces.Current.ShowsAccount(a.Id)))
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                var folders = workspaces.Current.FoldersOf(account.Id, await mail.GetFoldersAsync(connection.Id));
                var color = await AccountColors.GetAsync(settings, account.Id);
                var node = new MailAccountNode(account, connection, BuildTree(folders))
                {
                    IsSettingUp = _settingUp.Contains(connection.Id),
                    Tint = AccountColors.Tint(color),
                };
                node.ColorChoices = ColorChoicesFor(node, color);
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
    /// <summary>The account colors were changed in the settings: applied to the tree as it is.</summary>
    public async Task UpdateColorsAsync()
    {
        foreach (var node in Accounts)
        {
            var color = await AccountColors.GetAsync(settings, node.Account.Id);
            node.Tint = AccountColors.Tint(color);
            foreach (var choice in node.ColorChoices)
            {
                choice.IsChecked = choice.Color == color;
            }
        }
    }

    /// <summary>A color was chosen in the folder list (the settings show it too).</summary>
    public event EventHandler? ColorChanged;

    private List<AccountColorChoice> ColorChoicesFor(MailAccountNode node, string? color)
    {
        var choices = new[] { (string?)null }.Concat(AccountColors.Choices).Select(c => new AccountColorChoice(c) { IsChecked = c == color }).ToList();
        foreach (var choice in choices)
        {
            choice.Chosen = async () =>
            {
                await AccountColors.SetAsync(settings, node.Account.Id, choice.Color);
                await UpdateColorsAsync();
                ColorChanged?.Invoke(this, EventArgs.Empty);
            };
        }

        return choices;
    }

    public async Task<bool> UpdateCountsAsync()
    {
        var structure = new List<(MailAccountNode? Node, IReadOnlyList<MailFolder> Folders)>();
        var index = 0;
        foreach (var account in (await accounts.GetAccountsAsync()).Where(a => workspaces.Current.ShowsAccount(a.Id)))
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                var node = index < Accounts.Count && Accounts[index].Connection.Id == connection.Id && Accounts[index].Title == MailAccountNode.TitleOf(account) ? Accounts[index] : null;
                structure.Add((node, workspaces.Current.FoldersOf(account.Id, await mail.GetFoldersAsync(connection.Id))));
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
        OnPropertyChanged(nameof(SelectedFavoriteItem));
        OnPropertyChanged(nameof(SelectedAccountItem));

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

        if ((Favorites.Items.Count > 0) != (FavoriteRoots.Count > 0))
        {
            if (Favorites.Items.Count > 0)
            {
                FavoriteRoots.Add(Favorites);
            }
            else
            {
                FavoriteRoots.Clear();
            }

            OnPropertyChanged(nameof(HasFavorites));
        }
    }

    private void RebuildTreeRoots()
    {
        var selected = SelectedTreeItem;
        TreeRoots.Clear();
        FavoriteRoots.Clear();
        if (Favorites.Items.Count > 0)
        {
            TreeRoots.Add(Favorites);
            FavoriteRoots.Add(Favorites);
        }

        OnPropertyChanged(nameof(HasFavorites));

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
