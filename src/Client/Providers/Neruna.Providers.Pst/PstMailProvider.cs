using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Pst;

/// <summary>
/// An Outlook data file (.pst, also .ost) as a mail account – read only: the file is never changed. Read and flagged
/// are kept in Neruna (the next sync of an unchanged file leaves them); moving or deleting is not offered, copying
/// into another account is.
/// </summary>
public sealed class PstMailProvider(ServiceConnection connection) : IMailProvider
{
    /// <summary>The file's full path.</summary>
    public const string PathSetting = "pst.path";

    public MailProviderCapabilities Capabilities => MailProviderCapabilities.Flags | MailProviderCapabilities.LocalContent;

    private string FilePath => connection.Settings.GetValueOrDefault(PathSetting) is { Length: > 0 } path
        ? path
        : throw new PstException(T("Keine Outlook-Datendatei angegeben."));

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await PstArchive.For(FilePath).ReadAsync(a => a.Folders(), cancellationToken);

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        [.. (await PstArchive.For(FilePath).ReadAsync(a => a.Folders(), cancellationToken))
            .Select(f => new MailFolder(connection.Id, f.RemoteId, f.Name, f.ParentRemoteId, f.Role, f.Total, f.Unread))];

    public async Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(knownRemoteIds);
        var archive = PstArchive.For(FilePath);

        // The folder as at the last sync: nothing changed (and read/flagged set here stay as they are). The folder's own
        // counts and time, not the file's: Outlook writes to the file whenever it opens it, and every folder of a large
        // archive was read again for nothing.
        var state = "f:" + await archive.ReadAsync(a => PstArchive.StateOf(a.Folder(folder.RemoteId)), cancellationToken);
        if (folder.SyncState == state)
        {
            return new FolderSyncResult(state, false, [], new Dictionary<string, MessageFlags>(), [], folder.TotalCount, folder.UnreadCount);
        }

        // Second pass after a quick first one: the flags Outlook set (flagged, answered, forwarded).
        if (folder.SyncState == state + QuickPass)
        {
            var flagged = await OutlookFlagsAsync(archive, folder, cancellationToken);
            return new FolderSyncResult(state, false, [], flagged, [], folder.TotalCount, folder.UnreadCount);
        }

        // First pass: what the folder's contents table holds, so the mail shows at once (a few ms a message instead
        // of some 20 for everything) – the flags follow with the next sync.
        var summaries = await archive.ReadAsync(a =>
        {
            var source = a.Folder(folder.RemoteId);
            // A message that cannot be read does not stop the folder: it is left out.
            var list = new List<MessageSummary>();
            foreach (var (index, message) in PstArchive.Messages(source))
            {
                try
                {
                    list.Add(PstArchive.Read(message, m => PstArchive.Summary(index, m, full: false)));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    // damaged or of a kind XstReader cannot read
                }
            }

            return list;
        }, cancellationToken);
        var present = summaries.Select(s => s.RemoteId).ToHashSet(StringComparer.Ordinal);

        // Known ones keep what was set here; only the new ones (a newer copy of the file) come in.
        var added = summaries.Where(s => !knownRemoteIds.Contains(s.RemoteId)).ToList();
        return new FolderSyncResult(state + QuickPass, false, added, new Dictionary<string, MessageFlags>(),
            [.. knownRemoteIds.Where(k => !present.Contains(k))], summaries.Count, summaries.Count(s => !s.Flags.HasFlag(MessageFlags.Seen)));
    }

    /// <summary>Marks the sync state of a folder whose flags are still to be read.</summary>
    private const string QuickPass = "|quick";

    /// <summary>
    /// The messages Outlook flagged, answered or forwarded (with read as in the file). In portions, so that opening a
    /// message meanwhile does not wait for the whole folder.
    /// </summary>
    private static async Task<Dictionary<string, MessageFlags>> OutlookFlagsAsync(PstArchive archive, MailFolder folder, CancellationToken cancellationToken)
    {
        const int Portion = 200;
        var result = new Dictionary<string, MessageFlags>(StringComparer.Ordinal);
        for (var start = 0; ; start += Portion)
        {
            var done = await archive.ReadAsync(a =>
            {
                var portion = PstArchive.Messages(a.Folder(folder.RemoteId)).Skip(start).Take(Portion).ToList();
                foreach (var (index, message) in portion)
                {
                    try
                    {
                        var (id, flags) = PstArchive.Read(message, m => (PstArchive.RemoteIdOf(index, m), PstArchive.Flags(m)));
                        if ((flags & ~MessageFlags.Seen) != MessageFlags.None)
                        {
                            result[id] = flags;
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        // damaged or of a kind XstReader cannot read
                    }
                }

                return portion.Count < Portion;
            }, cancellationToken);
            if (done)
            {
                return result;
            }
        }
    }

    public async Task<MimeMessage> GetMessageAsync(MailFolder folder, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var item = await PstArchive.For(FilePath).ReadAsync(a => PstArchive.Read(PstArchive.Message(a.Folder(folder.RemoteId), remoteId), m => PstArchive.Item(m)), cancellationToken);
        return PstMime.Build(item);
    }

    // Read and flagged live in Neruna only: the file stays as it is.
    public Task SetFlagsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task MoveAsync(MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    public Task DeleteAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(T("Aus einer Outlook-Datendatei kann nicht gesendet werden."));

    public Task AppendAsync(MailFolder folder, MimeMessage message, MessageFlags flags, DateTimeOffset? receivedAt, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    // Everything is there after the first sync.
    public Task<IReadOnlyList<MessageSummary>> FetchOlderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, int count, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MessageSummary>>([]);

    public Task<(IReadOnlyList<MessageSummary> Hits, bool IsTruncated)> SearchAsync(MailFolder folder, MailSearchQuery query, int limit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // A file does not announce changes; a new copy is noticed at the next sync.
    public Task WaitForChangesAsync(MailFolder folder, CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromHours(1), cancellationToken);

    public Task<MailFolder> CreateFolderAsync(string name, FolderRole role, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    public Task<MailFolder> CreateSubfolderAsync(string name, MailFolder? parent, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    public Task RenameFolderAsync(MailFolder folder, string name, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    public Task DeleteFolderAsync(MailFolder folder, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    public Task EmptyAsync(MailFolder folder, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static NotSupportedException ReadOnly() =>
        new(T("Outlook-Datendateien (.pst) werden nur gelesen – Nachrichten lassen sich in ein anderes Konto kopieren."));
}

public sealed class PstMailProviderFactory : IProviderFactory<IMailProvider>
{
    public string ProviderId => ProviderIds.Pst;

    public string DisplayName => T("Outlook-Datendatei (.pst)");

    public IMailProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new PstMailProvider(connection);
    }

    public string Summary(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.GetValueOrDefault(PstMailProvider.PathSetting) ?? string.Empty;
    }

    /// <summary>An account for the file: named after it, without address (nothing is sent from it).</summary>
    public static Account AccountFor(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var title = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name.Trim();
        return new Account(Guid.NewGuid(), title, null,
            [new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Pst, new Dictionary<string, string> { [PstMailProvider.PathSetting] = path })]);
    }

    /// <summary>Closes the file of a removed account.</summary>
    public static void Close(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Settings.GetValueOrDefault(PstMailProvider.PathSetting) is { Length: > 0 } path)
        {
            PstArchive.Close(path);
        }
    }
}
