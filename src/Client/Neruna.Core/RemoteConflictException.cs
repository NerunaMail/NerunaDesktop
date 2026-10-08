namespace Neruna.Core;

/// <summary>
/// The server rejected a write because the item changed there since the last sync (e.g. HTTP 412 on an ETag check).
/// The UI should sync and let the user retry instead of overwriting someone else's change.
/// </summary>
public sealed class RemoteConflictException : Exception
{
    public RemoteConflictException()
        : base("Das Element wurde inzwischen auf dem Server geändert.")
    {
    }

    public RemoteConflictException(string message)
        : base(message)
    {
    }

    public RemoteConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
