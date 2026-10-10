using System.Globalization;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;

namespace Neruna.Client.Tests;

/// <summary>An in-memory "mail server" that the fake provider syncs against.</summary>
internal sealed class FakeMailServer
{
    private int _nextId = 1;

    public Dictionary<string, List<MessageSummary>> Folders { get; } = new() { ["INBOX"] = [] };

    /// <summary>Changing it simulates an IMAP UIDVALIDITY reset.</summary>
    public int Validity { get; set; } = 1;

    public int MessageDownloads { get; set; }

    /// <summary>Awaited at every folder listing (= every sync of a connection): lets a test hold a sync open.</summary>
    public Func<Task>? FolderListingGate { get; set; }

    private int _listingsRunning;

    public int FolderListings;

    /// <summary>The most folder listings that ran at the same time.</summary>
    public int MaxConcurrentListings;

    internal async Task ListingAsync()
    {
        Interlocked.Increment(ref FolderListings);
        var running = Interlocked.Increment(ref _listingsRunning);
        InterlockedMax(ref MaxConcurrentListings, running);
        try
        {
            if (FolderListingGate is { } gate)
            {
                await gate();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _listingsRunning);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>Complete messages stored with APPEND (drafts, moved mail), by remote ID.</summary>
    public Dictionary<string, MimeMessage> Contents { get; } = [];

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _version;

    /// <summary>Completes at the next delivery, like an IMAP IDLE notification.</summary>
    public Task Changed => _changed.Task;

    /// <summary>Counts deliveries, so a watcher notices one that happened while it was not waiting.</summary>
    public int Version => Volatile.Read(ref _version);

    public MessageSummary Deliver(string folder, string subject, DateTimeOffset date, MessageFlags flags = MessageFlags.None, string? messageId = null)
    {
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        var id = (_nextId++).ToString(CultureInfo.InvariantCulture);
        var message = new MessageSummary(id, messageId ?? $"<{id}@example.com>", null, subject, new MailAddress("Bob", "bob@example.com"), [], date, flags, 1000, false, "preview " + subject);
        Folders[folder].Add(message);
        return message;
    }

    public void SetFlags(string folder, string remoteId, MessageFlags flags)
    {
        var list = Folders[folder];
        var index = list.FindIndex(m => m.RemoteId == remoteId);
        list[index] = list[index] with { Flags = flags };
    }
}

internal sealed class FakeMailProviderFactory(FakeMailServer server) : IProviderFactory<IMailProvider>
{
    public const string Id = "fake";

    public string ProviderId => Id;

    public string DisplayName => "Fake";

    public IMailProvider Create(ServiceConnection connection) => new FakeMailProvider(connection.Id, server);
}

internal sealed class FakeMailProvider(Guid connectionId, FakeMailServer server) : IMailProvider
{
    public MailProviderCapabilities Capabilities => MailProviderCapabilities.Flags | MailProviderCapabilities.Send | MailProviderCapabilities.Append | MailProviderCapabilities.ServerSearch;

    public Task<IReadOnlyList<MessageSummary>> FetchOlderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, int count, CancellationToken cancellationToken = default)
    {
        var oldest = knownRemoteIds.Select(int.Parse).DefaultIfEmpty(int.MaxValue).Min();
        var older = server.Folders[folder.RemoteId].Where(m => int.Parse(m.RemoteId, CultureInfo.InvariantCulture) < oldest)
            .OrderBy(m => int.Parse(m.RemoteId, CultureInfo.InvariantCulture)).ToList();
        return Task.FromResult<IReadOnlyList<MessageSummary>>(older.Skip(Math.Max(0, older.Count - count)).OrderByDescending(m => m.Date).ToList());
    }

    public Task<(IReadOnlyList<MessageSummary> Hits, bool IsTruncated)> SearchAsync(MailFolder folder, MailSearchQuery query, int limit, CancellationToken cancellationToken = default)
    {
        var hits = server.Folders[folder.RemoteId].Where(query.Matches).OrderByDescending(m => m.Date).ToList();
        return Task.FromResult<(IReadOnlyList<MessageSummary>, bool)>((hits.Take(limit).ToList(), hits.Count > limit));
    }

    public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await server.ListingAsync();
        return Folders();
    }

    private IReadOnlyList<MailFolder> Folders() =>
        [.. server.Folders.Keys
            .Select(name => new MailFolder(connectionId, name, name, null, name switch
            {
                "INBOX" => FolderRole.Inbox,
                "Trash" => FolderRole.Trash,
                "Archive" => FolderRole.Archive,
                "Drafts" => FolderRole.Drafts,
                _ => FolderRole.None,
            }))];

    public Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default)
    {
        var messages = server.Folders[folder.RemoteId];
        var state = server.Validity.ToString(CultureInfo.InvariantCulture);
        var isFull = folder.SyncState != state;
        var known = knownRemoteIds.ToHashSet();

        var added = isFull ? messages : messages.Where(m => !known.Contains(m.RemoteId)).ToList();
        var flags = isFull ? [] : messages.Where(m => known.Contains(m.RemoteId)).ToDictionary(m => m.RemoteId, m => m.Flags);
        var removed = isFull ? [] : known.Except(messages.Select(m => m.RemoteId)).ToList();

        return Task.FromResult(new FolderSyncResult(state, isFull, added, flags, removed, messages.Count, messages.Count(m => !m.Flags.HasFlag(MessageFlags.Seen))));
    }

    public Task<MimeMessage> GetMessageAsync(MailFolder folder, string remoteId, CancellationToken cancellationToken = default)
    {
        server.MessageDownloads++;
        if (server.Contents.TryGetValue(remoteId, out var stored))
        {
            return Task.FromResult(stored);
        }

        var summary = server.Folders[folder.RemoteId].Single(m => m.RemoteId == remoteId);
        var message = new MimeMessage { Subject = summary.Subject, Body = new TextPart("plain") { Text = "Hallo " + summary.Subject } };
        message.From.Add(new MailboxAddress("Bob", "bob@example.com"));
        return Task.FromResult(message);
    }

    public Task SetFlagsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default)
    {
        foreach (var id in remoteIds)
        {
            var current = server.Folders[folder.RemoteId].Single(m => m.RemoteId == id).Flags;
            server.SetFlags(folder.RemoteId, id, add ? current | flags : current & ~flags);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default)
    {
        var moving = server.Folders[source.RemoteId].Where(m => remoteIds.Contains(m.RemoteId)).ToList();
        server.Folders[source.RemoteId].RemoveAll(m => remoteIds.Contains(m.RemoteId));
        server.Folders[target.RemoteId].AddRange(moving);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        server.Folders[folder.RemoteId].RemoveAll(m => remoteIds.Contains(m.RemoteId));
        return Task.CompletedTask;
    }

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync(MailFolder folder, MimeMessage message, MessageFlags flags, DateTimeOffset? receivedAt, CancellationToken cancellationToken = default)
    {
        var summary = server.Deliver(folder.RemoteId, message.Subject ?? string.Empty, receivedAt ?? DateTimeOffset.UtcNow, flags,
            message.MessageId is { } id ? $"<{id}>" : null);
        server.Contents[summary.RemoteId] = message;
        return Task.CompletedTask;
    }

    // Like IMAP: the first call returns at once (starts watching); later ones when something was delivered since.
    private int? _seenVersion;

    public async Task WaitForChangesAsync(MailFolder folder, CancellationToken cancellationToken)
    {
        var changed = server.Changed;
        if (_seenVersion is { } seen && seen == server.Version)
        {
            await changed.WaitAsync(cancellationToken);
        }

        _seenVersion = server.Version;
    }

    public Task<MailFolder> CreateFolderAsync(string name, FolderRole role, CancellationToken cancellationToken = default)
    {
        server.Folders.Add(name, []);
        return Task.FromResult(new MailFolder(connectionId, name, name, null, role));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
