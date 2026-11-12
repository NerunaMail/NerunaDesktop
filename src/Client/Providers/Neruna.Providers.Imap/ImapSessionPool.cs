using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace Neruna.Providers.Imap;

/// <summary>
/// Keeps signed-in IMAP connections for a moment after use, so the next action on the same account does not connect,
/// negotiate TLS and sign in again (half a second to two per action – deleting five mails in a row meant a dozen
/// sign-ins). Nothing about the connection changes: the same TLS session, the same sign-in, only kept open.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A connection is used by one provider at a time: <see cref="TakeAsync"/> hands it out, <see cref="Return"/>
/// takes it back. Two actions at once on one account get a connection each.</item>
/// <item>Accounts never share: the key is the connection's id and its server, port, security and user.</item>
/// <item>Before it is handed out again, a connection is asked (NOOP, a few seconds at most) whether it still answers;
/// one the server or a firewall dropped meanwhile is thrown away and the provider connects anew, as before.</item>
/// <item>Back in the pool no folder is selected (the provider deselects with UNSELECT, never CLOSE, which would
/// expunge), so the next use always selects afresh (current counters, UIDNEXT). A server without UNSELECT gets no
/// kept connections: everything as before.</item>
/// <item>Unused for <see cref="IdleTime"/>, a connection is signed out and closed; at most
/// <see cref="MaxIdlePerAccount"/> wait per account (servers limit connections per user).</item>
/// </list>
/// </remarks>
public sealed class ImapSessionPool : IDisposable
{
    /// <summary>How long an unused connection stays open.</summary>
    public static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(60);

    /// <summary>How many unused connections are kept per account.</summary>
    public const int MaxIdlePerAccount = 2;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Idle>> _idle = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Timer _sweep;
    private bool _disposed;

    public ImapSessionPool(ILogger<ImapSessionPool> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _sweep = new Timer(_ => CloseExpired(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>The key of an account's connections: never shared with another account or other settings.</summary>
    public static string KeyOf(Guid connectionId, ImapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Join('|', connectionId.ToString("N"), settings.ImapHost, settings.ImapPort, settings.ImapSecurity, settings.Username, settings.AcceptAnyCertificate);
    }

    /// <summary>Connections waiting right now (tests, diagnostics).</summary>
    public int IdleCount
    {
        get
        {
            lock (_gate)
            {
                return _idle.Values.Sum(l => l.Count);
            }
        }
    }

    /// <returns>A connection that is signed in and answers, or null: the caller connects itself.</returns>
    public async Task<ImapClient?> TakeAsync(string key, CancellationToken cancellationToken)
    {
        while (true)
        {
            Idle? candidate;
            lock (_gate)
            {
                if (_disposed || !_idle.TryGetValue(key, out var list) || list.Count == 0)
                {
                    return null;
                }

                // The one used last: the others can run out.
                candidate = list[^1];
                list.RemoveAt(list.Count - 1);
            }

            if (await AnswersAsync(candidate.Client, cancellationToken))
            {
                return candidate.Client;
            }

            Drop(candidate.Client);
        }
    }

    /// <summary>
    /// Takes a connection back after use (or closes it: broken, pool full or closed). The caller has made sure that no
    /// folder is selected any more. Never throws and never waits – closing happens in the background.
    /// </summary>
    public void Return(string key, ImapClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _ = Task.Run(() => ReturnAsync(key, client));
    }

    private async Task ReturnAsync(string key, ImapClient client)
    {
        try
        {
            if (_disposed || !client.IsConnected || !client.IsAuthenticated || client.IsIdle)
            {
                await CloseAsync(client);
                return;
            }

            lock (_gate)
            {
                if (!_disposed && client.IsConnected)
                {
                    if (!_idle.TryGetValue(key, out var list))
                    {
                        _idle[key] = list = [];
                    }

                    if (list.Count < MaxIdlePerAccount)
                    {
                        list.Add(new Idle(client, _time.GetUtcNow()));
                        return;
                    }
                }
            }

            await CloseAsync(client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "An IMAP connection was not kept for reuse");
            Drop(client);
        }
    }

    /// <summary>The account is gone or its settings changed: its waiting connections are closed.</summary>
    public void Forget(Guid connectionId)
    {
        var prefix = connectionId.ToString("N") + "|";
        List<ImapClient> closing = [];
        lock (_gate)
        {
            foreach (var key in _idle.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                closing.AddRange(_idle[key].Select(i => i.Client));
                _idle.Remove(key);
            }
        }

        foreach (var client in closing)
        {
            _ = Task.Run(() => CloseAsync(client));
        }
    }

    private void CloseExpired()
    {
        List<ImapClient> expired = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var before = _time.GetUtcNow() - IdleTime;
            foreach (var (key, list) in _idle.ToList())
            {
                expired.AddRange(list.Where(i => i.Since < before).Select(i => i.Client));
                list.RemoveAll(i => i.Since < before);
                if (list.Count == 0)
                {
                    _idle.Remove(key);
                }
            }
        }

        foreach (var client in expired)
        {
            _ = Task.Run(() => CloseAsync(client));
        }
    }

    private async Task<bool> AnswersAsync(ImapClient client, CancellationToken cancellationToken)
    {
        if (!client.IsConnected || !client.IsAuthenticated)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            await client.NoOpAsync(timeout.Token);
            return client.IsConnected && client.IsAuthenticated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Drop(client);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "A kept IMAP connection no longer answers; connecting anew");
            return false;
        }
    }

    // Signs out properly (the server frees the session at once), but never waits long for a server that is gone.
    private async Task CloseAsync(ImapClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await client.DisconnectAsync(true, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Ignoring error while closing a kept IMAP connection");
        }
        finally
        {
            Drop(client);
        }
    }

    private void Drop(ImapClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Ignoring error while disposing an IMAP connection");
        }
    }

    /// <summary>Neruna ends: the waiting connections are closed without waiting for the servers.</summary>
    public void Dispose()
    {
        List<ImapClient> all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _idle.Values.SelectMany(l => l.Select(i => i.Client))];
            _idle.Clear();
        }

        _sweep.Dispose();
        foreach (var client in all)
        {
            Drop(client);
        }
    }

    private sealed record Idle(ImapClient Client, DateTimeOffset Since);
}
