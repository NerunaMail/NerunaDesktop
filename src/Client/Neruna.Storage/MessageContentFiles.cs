using System.Security.Cryptography;
using System.Text;

namespace Neruna.Storage;

/// <summary>
/// Raw MIME cache on disk: <c>messages/&lt;connection&gt;/&lt;folder hash&gt;/&lt;remote id&gt;.eml</c>.
/// Folder names and remote ids are hashed so arbitrary server names never become unsafe paths.
/// </summary>
public sealed class MessageContentFiles(string rootDirectory)
{
    private readonly string _root = Path.Combine(rootDirectory, "messages");

    public string PathFor(Guid connectionId, string folderRemoteId, string remoteId) =>
        Path.Combine(FolderDirectory(connectionId, folderRemoteId), Hash(remoteId) + ".eml");

    public void DeleteMessages(Guid connectionId, string folderRemoteId, IEnumerable<string> remoteIds)
    {
        foreach (var remoteId in remoteIds)
        {
            // Most messages were never downloaded; File.Delete throws if even the folder directory is missing.
            var path = PathFor(connectionId, folderRemoteId, remoteId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void DeleteFolder(Guid connectionId, string folderRemoteId) =>
        DeleteDirectory(FolderDirectory(connectionId, folderRemoteId));

    public void DeleteConnection(Guid connectionId) =>
        DeleteDirectory(Path.Combine(_root, connectionId.ToString("N")));

    private string FolderDirectory(Guid connectionId, string folderRemoteId) =>
        Path.Combine(_root, connectionId.ToString("N"), Hash(folderRemoteId));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}
