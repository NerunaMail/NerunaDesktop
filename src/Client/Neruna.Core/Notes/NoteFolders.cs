using Neruna.Core.Mail;

namespace Neruna.Core.Notes;

/// <summary>
/// The notes folder of a mailbox: "Notes" at the top (Dovecot with the "INBOX." prefix: under the inbox), as the
/// phone apps name it; its subfolders are the notebooks. It is no mail folder – the mail view leaves it out.
/// </summary>
public static class NoteFolders
{
    public const string RootName = "Notes";

    public static MailFolder? Root(IReadOnlyList<MailFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var inbox = folders.FirstOrDefault(f => f.Role == FolderRole.Inbox)?.RemoteId;
        return folders.Where(f => string.Equals(f.Name, RootName, StringComparison.OrdinalIgnoreCase) && f.Role == FolderRole.None
                                  && (f.ParentRemoteId is null || f.ParentRemoteId == inbox))
            .OrderBy(f => f.ParentRemoteId is null ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>The notes folder and its subfolders, each with its notebook path ("" for the folder itself).</summary>
    public static IReadOnlyList<(MailFolder Folder, string Notebook)> All(IReadOnlyList<MailFolder> folders)
    {
        if (Root(folders) is not { } root)
        {
            return [];
        }

        var result = new List<(MailFolder, string)> { (root, string.Empty) };
        var byParent = folders.ToLookup(f => f.ParentRemoteId);
        void Add(MailFolder parent, string path)
        {
            foreach (var child in byParent[parent.RemoteId])
            {
                var childPath = path.Length == 0 ? child.Name : path + "/" + child.Name;
                result.Add((child, childPath));
                Add(child, childPath);
            }
        }

        Add(root, string.Empty);
        return result;
    }

    /// <summary>The mail folders without the notes folder and its subfolders.</summary>
    public static IReadOnlyList<MailFolder> WithoutNotes(IReadOnlyList<MailFolder> folders)
    {
        var notes = All(folders).Select(n => n.Folder.RemoteId).ToHashSet(StringComparer.Ordinal);
        return notes.Count == 0 ? folders : [.. folders.Where(f => !notes.Contains(f.RemoteId))];
    }

    public static bool IsNotesFolder(MailFolder folder, IReadOnlyList<MailFolder> folders) =>
        All(folders).Any(n => n.Folder.RemoteId == folder?.RemoteId);
}
