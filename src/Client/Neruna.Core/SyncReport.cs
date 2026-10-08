using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;

namespace Neruna.Core;

/// <summary>Outcome of syncing several connections; one failing connection never stops the others.</summary>
public sealed class SyncReport
{
    private readonly List<(ServiceConnection Connection, Exception Error)> _failures = [];
    private readonly List<ServiceConnection> _skipped = [];

    public int SucceededConnections { get; private set; }

    public IReadOnlyList<(ServiceConnection Connection, Exception Error)> Failures => _failures;

    /// <summary>Connections whose provider is not available in this build (e.g. CalDAV before it ships).</summary>
    public IReadOnlyList<ServiceConnection> Skipped => _skipped;

    /// <summary>Runs <paramref name="sync"/> for every connection of <paramref name="kind"/> across all accounts.</summary>
    internal static async Task<SyncReport> ForEachConnectionAsync(
        IAccountStore accounts,
        ServiceKind kind,
        Func<ServiceConnection, CancellationToken, Task> sync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var report = new SyncReport();
        foreach (var account in await accounts.GetAccountsAsync(cancellationToken))
        {
            foreach (var connection in account.ConnectionsOf(kind))
            {
                try
                {
                    await sync(connection, cancellationToken);
                    report.SucceededConnections++;
                }
                catch (ProviderNotFoundException)
                {
                    report._skipped.Add(connection);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "{Kind} sync failed for connection {ConnectionId}", kind, connection.Id);
                    report._failures.Add((connection, ex));
                }
            }
        }

        return report;
    }
}
