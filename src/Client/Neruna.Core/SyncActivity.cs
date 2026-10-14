using Neruna.Core.Accounts;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core;

public enum SyncState
{
    /// <summary>Being synchronised right now.</summary>
    Running,

    /// <summary>The last run went through.</summary>
    Done,

    /// <summary>The last run failed (see <see cref="SyncEntry.Error"/>).</summary>
    Failed,

    /// <summary>Not synchronised in this build (no provider for it).</summary>
    Skipped,
}

/// <summary>The state of one connection (an account's mail, calendars or contacts).</summary>
/// <param name="StartedAt">When the current or last run began.</param>
/// <param name="Duration">How long the last run took; null while it runs.</param>
/// <param name="LastSuccess">When a run last went through.</param>
public sealed record SyncEntry(
    Guid ConnectionId,
    Guid AccountId,
    string Account,
    ServiceKind Kind,
    string ProviderId,
    SyncState State,
    DateTimeOffset StartedAt,
    TimeSpan? Duration,
    string? Error,
    DateTimeOffset? LastSuccess);

/// <summary>
/// What the synchronisation is doing, per connection: which runs, since when, how the last run ended – for the status
/// window ("which account takes so long?"). Mail, calendars and contacts run at the same time, so it is thread-safe;
/// <see cref="Changed"/> is raised on the syncing thread.
/// </summary>
public sealed class SyncActivity(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, SyncEntry> _entries = [];

    public event EventHandler? Changed;

    /// <summary>All connections seen so far, by account and service.</summary>
    public IReadOnlyList<SyncEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values.OrderBy(e => e.Account, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Kind)];
            }
        }
    }

    internal void Started(Account account, ServiceConnection connection) =>
        Update(connection.Id, previous => new SyncEntry(connection.Id, account.Id, account.Title, connection.Kind, connection.ProviderId,
            SyncState.Running, time.GetUtcNow(), null, null, previous?.LastSuccess));

    internal void Finished(ServiceConnection connection, Exception? error) =>
        Update(connection.Id, previous => previous is null ? null : previous with
        {
            State = error is null ? SyncState.Done : SyncState.Failed,
            Duration = time.GetUtcNow() - previous.StartedAt,
            Error = error is null ? null : Describe(error),
            LastSuccess = error is null ? time.GetUtcNow() : previous.LastSuccess,
        });

    /// <summary>The error for the status window; a timeout in words instead of .NET's "The request was canceled …".</summary>
    internal static string Describe(Exception error) =>
        error is OperationCanceledException or TimeoutException || error.InnerException is TimeoutException
            ? T("Zeitüberschreitung: Der Server hat nicht rechtzeitig geantwortet.")
            : error.Message;

    internal void Skipped(ServiceConnection connection) =>
        Update(connection.Id, previous => previous is null ? null : previous with { State = SyncState.Skipped, Duration = TimeSpan.Zero });

    /// <summary>Connections of accounts that are gone are forgotten.</summary>
    public void Forget(IEnumerable<Guid> keepConnections)
    {
        var keep = keepConnections.ToHashSet();
        lock (_gate)
        {
            foreach (var id in _entries.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                _entries.Remove(id);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Update(Guid connectionId, Func<SyncEntry?, SyncEntry?> change)
    {
        lock (_gate)
        {
            if (change(_entries.GetValueOrDefault(connectionId)) is { } entry)
            {
                _entries[connectionId] = entry;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
