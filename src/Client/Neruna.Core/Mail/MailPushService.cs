using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;

namespace Neruna.Core.Mail;

/// <summary>New unread messages that arrived in an inbox.</summary>
public sealed record NewMailEvent(Account Account, ServiceConnection Connection, MailFolder Folder, IReadOnlyList<MessageSummary> Messages);

/// <summary>
/// Watches the inbox of every mail account so new mail shows up within seconds: one connection per account waits in
/// IMAP IDLE (servers without IDLE are polled), then the inbox is synced and new unread messages are reported.
/// Lost connections are retried with growing pauses (30 s up to 5 min). The periodic full sync stays in charge of the
/// other folders.
/// </summary>
public sealed class MailPushService(
    MailController mail,
    IMailStore store,
    IAccountStore accounts,
    ProviderRegistry providers,
    ILogger<MailPushService> logger) : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private readonly List<Task> _watches = [];
    private CancellationTokenSource? _stop;

    /// <summary>New unread messages (not raised for the very first sync of an inbox). Raised on a background thread.</summary>
    public event EventHandler<NewMailEvent>? NewMail;

    /// <summary>An inbox was synced (messages added, removed or changed). Raised on a background thread.</summary>
    public event EventHandler<MailFolder>? FolderSynced;

    public bool IsRunning => _stop is not null;

    /// <summary>Starts watching all mail accounts (again – after accounts changed, call it once more).</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync();
        _stop = new CancellationTokenSource();
        foreach (var account in await accounts.GetAccountsAsync(cancellationToken))
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail).Where(c => providers.HasMailProvider(c.ProviderId)))
            {
                var token = _stop.Token;
                _watches.Add(Task.Run(() => WatchAsync(account, connection, token), CancellationToken.None));
            }
        }
    }

    public async Task StopAsync()
    {
        if (_stop is null)
        {
            return;
        }

        await _stop.CancelAsync();
        await Task.WhenAll(_watches);
        _watches.Clear();
        _stop.Dispose();
        _stop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>On shutdown: stops the watches without waiting for their connections to close.</summary>
    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }

    private async Task WatchAsync(Account account, ServiceConnection connection, CancellationToken cancellationToken)
    {
        var retry = FirstRetry;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var provider = providers.CreateMail(connection);
                if (await mail.FindFolderAsync(connection.Id, FolderRole.Inbox, cancellationToken) is not { } inbox)
                {
                    // Not synced yet; the periodic sync will create the folder list.
                    await Task.Delay(FirstRetry, cancellationToken);
                    continue;
                }

                logger.LogInformation("Watching the inbox of {Account}", account.EmailAddress);

                // Catch up first – mail may have arrived while nobody was watching. That is not reported (no flood of
                // notifications at startup); from then on everything new is, whoever synced it first.
                var known = await CheckAsync(account, connection, inbox, null, cancellationToken);
                while (true)
                {
                    await provider.WaitForChangesAsync(inbox, cancellationToken);
                    known = await CheckAsync(account, connection, inbox, known, cancellationToken);
                    retry = FirstRetry;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogInformation(ex, "Watching the inbox of {Account} failed; retrying in {Delay}", account.EmailAddress, retry);
                try
                {
                    await Task.Delay(retry, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            }
        }
    }

    /// <param name="known">Messages already seen by this watch; null at its start (nothing is reported then).</param>
    /// <returns>The messages known now.</returns>
    private async Task<HashSet<string>> CheckAsync(Account account, ServiceConnection connection, MailFolder inbox, HashSet<string>? known, CancellationToken cancellationToken)
    {
        var current = await mail.FindFolderAsync(connection.Id, FolderRole.Inbox, cancellationToken) ?? inbox;
        await mail.SyncFolderAsync(connection, current, cancellationToken);

        var now = (await store.GetMessageIdsAsync(connection.Id, current.RemoteId, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        FolderSynced?.Invoke(this, current);
        if (known is not null)
        {
            var newest = await store.GetMessagesAsync(connection.Id, current.RemoteId, 0, 50, cancellationToken);
            var fresh = newest.Where(m => !known.Contains(m.RemoteId) && !m.Flags.HasFlag(MessageFlags.Seen)).ToList();
            logger.LogDebug("Inbox of {Account} checked: {New} new unread", account.EmailAddress, fresh.Count);
            if (fresh.Count > 0)
            {
                logger.LogInformation("{Count} new messages for {Account}", fresh.Count, account.EmailAddress);
                NewMail?.Invoke(this, new NewMailEvent(account, connection, current, fresh));
            }
        }

        return now;
    }
}
