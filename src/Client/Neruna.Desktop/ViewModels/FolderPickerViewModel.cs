using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;

namespace Neruna.Desktop.ViewModels;

/// <summary>A folder found by the search: its path, and the account it belongs to.</summary>
internal sealed record FolderPickItem(MailFolderNode Node, string Name, string Account)
{
    public FolderRole Role => Node.Folder.Role;
}

/// <summary>A line in the picker's tree: an account (not pickable, only opens and closes) or one of its folders.</summary>
internal sealed partial class FolderPickNode : ObservableObject
{
    public FolderPickNode(string key, string name, MailFolderNode? folder, bool isEnabled)
    {
        Key = key;
        Name = name;
        Folder = folder;
        IsEnabled = isEnabled;
    }

    /// <summary>"a:{connection}" for an account, "{connection}|{folder}" for a folder (as kept in the settings).</summary>
    public string Key { get; }

    public string Name { get; }

    public MailFolderNode? Folder { get; }

    public bool IsAccount => Folder is null;

    /// <summary>False for the folder the mail is already in (it stays in the tree for its subfolders).</summary>
    public bool IsEnabled { get; }

    public FolderRole Role => Folder?.Folder.Role ?? FolderRole.None;

    public ObservableCollection<FolderPickNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>
/// "Verschieben nach …" / "Kopieren nach …" (also a quick action «Verschieben», asking each time): the folders of all
/// accounts as a tree – opened and closed as last time –, and a search box that lists the matching folders flat.
/// A click on a folder picks it; Enter picks the only one the search left.
/// </summary>
internal sealed partial class FolderPickerViewModel : ViewModelBase
{
    private readonly MailFolderNode? _current;
    private readonly List<(MailFolderNode Node, string Path, string Account)> _all = [];

    /// <param name="expanded">The nodes open last time; null the first time (then the accounts and the way to the
    /// current folder are open).</param>
    public FolderPickerViewModel(IReadOnlyList<MailAccountNode> accounts, MailFolderNode? current, string title, IReadOnlyCollection<string>? expanded = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _current = current;
        Title = title;
        var open = expanded is null ? null : new HashSet<string>(expanded, StringComparer.Ordinal);
        var path = current is null ? [] : new HashSet<MailFolderNode>(PathTo(current, accounts));
        foreach (var account in accounts.Where(a => !a.IsReadOnly))
        {
            var node = new FolderPickNode(AccountKey(account), account.Title, null, isEnabled: true)
            {
                IsExpanded = open?.Contains(AccountKey(account)) ?? true,
            };
            Add(node.Children, account.Folders, string.Empty, account.Title, open, path);
            Roots.Add(node);
        }
    }

    public event EventHandler<MailFolderNode?>? Finished;

    /// <summary>"In welchen Ordner verschieben?", "In welchen Ordner kopieren?".</summary>
    public string Title { get; }

    public ObservableCollection<FolderPickNode> Roots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    public partial string Filter { get; set; } = string.Empty;

    public bool IsSearching => Filter.Trim().Length > 0;

    /// <summary>While searching: the matching folders of all accounts, with their path.</summary>
    public ObservableCollection<FolderPickItem> Matches { get; } = [];

    public bool HasNoMatch => IsSearching && Matches.Count == 0;

    /// <summary>The open nodes now – kept in the settings, so the tree opens the same way next time.</summary>
    public IReadOnlyList<string> ExpandedKeys => [.. Walk(Roots).Where(n => n.IsExpanded).Select(n => n.Key)];

    internal static string AccountKey(MailAccountNode account) => "a:" + account.Connection.Id.ToString("N");

    internal static string FolderKey(MailFolder folder) => $"{folder.ConnectionId:N}|{folder.RemoteId}";

    private void Add(ObservableCollection<FolderPickNode> into, IEnumerable<MailFolderNode> folders, string parent, string account,
        HashSet<string>? open, HashSet<MailFolderNode> pathToCurrent)
    {
        foreach (var folder in folders)
        {
            var key = FolderKey(folder.Folder);
            var node = new FolderPickNode(key, folder.Name, folder, isEnabled: folder != _current)
            {
                IsExpanded = open?.Contains(key) ?? pathToCurrent.Contains(folder),
            };
            var path = parent.Length == 0 ? folder.Name : parent + " / " + folder.Name;
            if (folder != _current)
            {
                _all.Add((folder, path, account));
            }

            Add(node.Children, folder.Children, path, account, open, pathToCurrent);
            into.Add(node);
        }
    }

    // The folders above the current one (opened the first time, so it is in view).
    private static List<MailFolderNode> PathTo(MailFolderNode current, IReadOnlyList<MailAccountNode> accounts)
    {
        foreach (var account in accounts)
        {
            if (Find(account.Folders) is { } path)
            {
                return path;
            }
        }

        return [];

        List<MailFolderNode>? Find(IEnumerable<MailFolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node == current)
                {
                    return [];
                }

                if (Find(node.Children) is { } below)
                {
                    below.Insert(0, node);
                    return below;
                }
            }

            return null;
        }
    }

    private static IEnumerable<FolderPickNode> Walk(IEnumerable<FolderPickNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Children))
            {
                yield return child;
            }
        }
    }

    partial void OnFilterChanged(string value)
    {
        Matches.Clear();
        var text = value.Trim();
        if (text.Length > 0)
        {
            // A flat list: the path tells "Projekte / 2026" from "Archiv / 2026", the account one mailbox from another.
            foreach (var (node, path, account) in _all.Where(f => f.Node.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            {
                Matches.Add(new FolderPickItem(node, path, account));
            }
        }

        OnPropertyChanged(nameof(HasNoMatch));
    }

    /// <summary>A folder in the tree: picked; an account: opened or closed.</summary>
    [RelayCommand]
    private void PickNode(FolderPickNode? node)
    {
        if (node is null || !node.IsEnabled)
        {
            return;
        }

        if (node.Folder is { } folder)
        {
            Finished?.Invoke(this, folder);
        }
        else
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    [RelayCommand]
    private void Pick(FolderPickItem? item)
    {
        if (item is not null)
        {
            Finished?.Invoke(this, item.Node);
        }
    }

    /// <summary>Enter in the search box: the folder, if the search left exactly one.</summary>
    [RelayCommand]
    private void PickOnly()
    {
        if (Matches.Count == 1)
        {
            Pick(Matches[0]);
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, null);
}
