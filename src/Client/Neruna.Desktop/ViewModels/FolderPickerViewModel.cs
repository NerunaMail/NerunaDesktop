using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;

namespace Neruna.Desktop.ViewModels;

/// <summary>A folder in the picker: indented as in the tree, or with its path while searching.</summary>
internal sealed record FolderPickItem(MailFolderNode Node, string Name, string Path, int Depth)
{
    public FolderRole Role => Node.Folder.Role;

    public Avalonia.Thickness Indent => new(Depth * 16, 0, 0, 0);
}

/// <summary>
/// "In welchen Ordner?" (Schnellaktion «Verschieben», asking each time): the account – the mail's own preselected –,
/// a search box and the folders. A click on a folder picks it; Enter picks the only one the search left.
/// </summary>
internal sealed partial class FolderPickerViewModel : ViewModelBase
{
    private readonly MailFolderNode? _current;

    public FolderPickerViewModel(IReadOnlyList<MailAccountNode> accounts, MailAccountNode account, MailFolderNode? current)
    {
        Accounts = [.. accounts.Where(a => !a.IsReadOnly)];
        _current = current;
        SelectedAccount = Accounts.Contains(account) ? account : Accounts.FirstOrDefault();
    }

    public event EventHandler<MailFolderNode?>? Finished;

    public IReadOnlyList<MailAccountNode> Accounts { get; }

    public bool HasSeveralAccounts => Accounts.Count > 1;

    [ObservableProperty]
    public partial MailAccountNode? SelectedAccount { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    public ObservableCollection<FolderPickItem> Folders { get; } = [];

    public bool HasNoMatch => Folders.Count == 0;

    partial void OnSelectedAccountChanged(MailAccountNode? value) => Update();

    partial void OnFilterChanged(string value) => Update();

    private void Update()
    {
        Folders.Clear();
        if (SelectedAccount is { } account)
        {
            var text = Filter.Trim();
            foreach (var item in Flatten(account.Folders, string.Empty, 0))
            {
                if (item.Node == _current)
                {
                    continue; // the mail is already there
                }

                if (text.Length == 0)
                {
                    Folders.Add(item);
                }
                else if (item.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                {
                    // While searching a flat list: the path tells "Projekte / 2026" from "Archiv / 2026".
                    Folders.Add(item with { Name = item.Path, Depth = 0 });
                }
            }
        }

        OnPropertyChanged(nameof(HasNoMatch));
    }

    private static IEnumerable<FolderPickItem> Flatten(IEnumerable<MailFolderNode> nodes, string parent, int depth)
    {
        foreach (var node in nodes)
        {
            var path = parent.Length == 0 ? node.Name : parent + " / " + node.Name;
            yield return new FolderPickItem(node, node.Name, path, depth);
            foreach (var child in Flatten(node.Children, path, depth + 1))
            {
                yield return child;
            }
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
        if (Folders.Count == 1)
        {
            Pick(Folders[0]);
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, null);
}
