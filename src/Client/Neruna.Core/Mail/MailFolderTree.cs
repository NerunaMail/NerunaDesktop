using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>A folder in the order of the folder list, with its depth and whether it has subfolders.</summary>
public sealed record MailFolderTreeItem(MailFolder Folder, int Depth, MailFolder? Parent, bool HasChildren);

/// <summary>How folders are named and ordered in the folder lists (desktop and app alike).</summary>
public static class MailFolderTree
{
    /// <summary>Special folders get familiar names, whatever the server calls them (INBOX, Sent, Trash …).</summary>
    public static string DisplayName(MailFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.Role switch
        {
            FolderRole.Inbox => T("Posteingang"),
            FolderRole.Drafts => T("Entwürfe"),
            FolderRole.Sent => T("Gesendete Elemente"),
            FolderRole.Trash => T("Gelöschte Elemente"),
            FolderRole.Junk => T("Junk-E-Mail"),
            FolderRole.Archive => T("Archiv"),
            _ => folder.Name,
        };
    }

    /// <summary>
    /// Special folders first (by role), each with its subfolders (Dovecot: INBOX.Projekte …), then the other
    /// top-level folders A–Z as trees; subfolders A–Z.
    /// </summary>
    public static IReadOnlyList<MailFolderTreeItem> Order(IReadOnlyList<MailFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var result = new List<MailFolderTreeItem>();
        var byParent = folders.ToLookup(f => f.ParentRemoteId);
        var known = folders.Select(f => f.RemoteId).ToHashSet(StringComparer.Ordinal);
        void Add(MailFolder folder, int depth, MailFolder? parent)
        {
            var children = byParent[folder.RemoteId].Where(f => f.Role == FolderRole.None).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            result.Add(new MailFolderTreeItem(folder, depth, parent, children.Count > 0));
            foreach (var child in children)
            {
                Add(child, depth + 1, folder);
            }
        }

        foreach (var folder in folders.Where(f => f.Role != FolderRole.None).OrderBy(f => f.Role))
        {
            Add(folder, 0, null);
        }

        foreach (var folder in folders.Where(f => f.Role == FolderRole.None && (f.ParentRemoteId is null || !known.Contains(f.ParentRemoteId))).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Add(folder, 0, null);
        }

        return result;
    }
}
