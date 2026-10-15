using System.Text;
using Microsoft.Extensions.Logging;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;

namespace Neruna.Core.Mail;

/// <summary>An account in the search index: whether it is indexed and how far.</summary>
/// <param name="ServerText">Whether the text of mail only on the server is fetched for the index; null: the account's server cannot.</param>
public sealed record SearchIndexAccount(Account Account, bool IsEnabled, SearchIndexStatus Status, bool? ServerText);

/// <summary>
/// Builds the local full-text index of the mail in the background, so searching answers at once – also for messages
/// not loaded in the list. First sender, recipients and subject of every message (quick, from the stored list), then
/// the text of those whose content is on this device (opened, kept offline, or a local file like an Outlook archive).
/// Mail only on the server is found by its text through the server's search. Indexing can be switched off per account
/// (Einstellungen → Suchindex); its index is then removed.
/// </summary>
public sealed class MailSearchIndexer : IDisposable
{
    private const int HeaderBatch = 500;
    private const int TextBatch = 25;
    private const int ServerBatch = 50;
    private const int MaxTextLength = 200_000;

    private readonly IMailSearchIndex _index;
    private readonly IMailStore _store;
    private readonly IAccountStore _accounts;
    private readonly ProviderRegistry _providers;
    private readonly ISettingsStore _settings;
    private readonly ILogger<MailSearchIndexer> _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _started;

    public MailSearchIndexer(IMailSearchIndex index, IMailStore store, IAccountStore accounts, ProviderRegistry providers, ISettingsStore settings,
        MailController mail, ILogger<MailSearchIndexer> logger)
    {
        ArgumentNullException.ThrowIfNull(mail);
        _index = index;
        _store = store;
        _accounts = accounts;
        _providers = providers;
        _settings = settings;
        _logger = logger;
        mail.FoldersSynced += (_, _) => Poke();
    }

    /// <summary>Something was indexed (the settings page shows the new state).</summary>
    public event EventHandler? Progress;

    /// <summary>Indexing right now.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Starts the background work (once); it runs when poked and every few minutes.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(LoopAsync);
        }
    }

    /// <summary>New mail or content: index it soon.</summary>
    public void Poke()
    {
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    private async Task LoopAsync()
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stop);

                // Opened messages bring their text: looked at every few minutes, at once after a sync.
                await _signal.WaitAsync(TimeSpan.FromMinutes(3), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Search indexing interrupted");
                await Task.Delay(TimeSpan.FromMinutes(1), stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Indexes everything that is waiting (also for tests).</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken);
        IsBusy = true;
        try
        {
            var connections = await IndexedConnectionsAsync(cancellationToken);
            if (connections.Count == 0)
            {
                return;
            }

            var local = connections.Where(c => c.Value).Select(c => c.Key).ToHashSet();
            var ids = connections.Keys.ToList();

            // Sender, recipients and subject of every message: from the stored list, thousands per second.
            IReadOnlyList<SearchWork> work;
            while ((work = await _index.GetSearchWorkAsync(ids, 0, HeaderBatch, cancellationToken)).Count > 0)
            {
                await _index.SaveSearchEntriesAsync(
                    [.. work.Select(w => (w.Id, Document(w.Summary, null, null), w.HasContent || local.Contains(w.Folder.ConnectionId) ? 2 : 1))], cancellationToken);
                Progress?.Invoke(this, EventArgs.Empty);
                await Task.Delay(10, cancellationToken);
            }

            // The text where it is on this device; a few at a time, so the app stays responsive.
            var all = (await _accounts.GetAccountsAsync(cancellationToken)).SelectMany(a => a.Connections).ToDictionary(c => c.Id);
            while ((work = await _index.GetSearchWorkAsync(ids, 2, TextBatch, cancellationToken)).Count > 0)
            {
                var entries = new List<(long, SearchDocument, int)>();
                foreach (var item in work)
                {
                    var message = await ReadAsync(item, all, cancellationToken);
                    entries.Add(message is null
                        ? (item.Id, Document(item.Summary, null, null), 1)
                        : (item.Id, Document(item.Summary, TextOf(message), AttachmentNames(message)), 3));
                }

                await _index.SaveSearchEntriesAsync(entries, cancellationToken);
                Progress?.Invoke(this, EventArgs.Empty);
                await Task.Delay(20, cancellationToken);
            }

            await IndexServerTextAsync(all, cancellationToken);
        }
        finally
        {
            IsBusy = false;
            _running.Release();
            Progress?.Invoke(this, EventArgs.Empty);
        }
    }

    // The text of mail only on the server (IMAP): text parts only, a batch per folder, with pauses – the server
    // and the line are shared with the work of the user. Offline or refused: stops, the next run goes on.
    private async Task IndexServerTextAsync(Dictionary<Guid, ServiceConnection> all, CancellationToken cancellationToken)
    {
        var servers = new List<Guid>();
        foreach (var account in await _accounts.GetAccountsAsync(cancellationToken))
        {
            if (await IsEnabledAsync(account.Id, cancellationToken) && await IsServerTextEnabledAsync(account.Id, cancellationToken))
            {
                servers.AddRange(account.ConnectionsOf(ServiceKind.Mail).Where(c => SupportsServerText(c)).Select(c => c.Id));
            }
        }

        foreach (var connectionId in servers)
        {
            await using var provider = _providers.CreateMail(all[connectionId]);
            IReadOnlyList<SearchWork> work;
            while ((work = await _index.GetSearchWorkAsync([connectionId], 1, ServerBatch, cancellationToken)).Count > 0)
            {
                var entries = new List<(long, SearchDocument, int)>();
                try
                {
                    foreach (var folder in work.GroupBy(w => w.Folder.RemoteId))
                    {
                        var texts = await provider.GetSearchTextsAsync(folder.First().Folder, [.. folder.Select(w => w.Summary.RemoteId)], cancellationToken);
                        entries.AddRange(folder.Select(w => texts.TryGetValue(w.Summary.RemoteId, out var text)
                            ? (w.Id, Document(w.Summary, text.Body, text.Attachments), 3)
                            : (w.Id, Document(w.Summary, null, null), 4)));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _logger.LogInformation(ex, "Server text for the search index not fetched now");
                    await _index.SaveSearchEntriesAsync(entries, cancellationToken);
                    break;
                }

                await _index.SaveSearchEntriesAsync(entries, cancellationToken);
                Progress?.Invoke(this, EventArgs.Empty);
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    private bool SupportsServerText(ServiceConnection connection)
    {
        try
        {
            var provider = _providers.CreateMail(connection);
            var supports = provider.Capabilities.HasFlag(MailProviderCapabilities.SearchText);
            _ = provider.DisposeAsync().AsTask();
            return supports;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private async Task<MimeMessage?> ReadAsync(SearchWork item, Dictionary<Guid, ServiceConnection> connections, CancellationToken cancellationToken)
    {
        try
        {
            await using (var stored = await _store.OpenMessageContentAsync(item.Folder.ConnectionId, item.Folder.RemoteId, item.Summary.RemoteId, cancellationToken))
            {
                if (stored is not null)
                {
                    return await MimeMessage.LoadAsync(stored, cancellationToken);
                }
            }

            // A local file (Outlook archive): read from it directly, nothing is kept.
            if (connections.TryGetValue(item.Folder.ConnectionId, out var connection))
            {
                await using var provider = _providers.CreateMail(connection);
                if (provider.Capabilities.HasFlag(MailProviderCapabilities.LocalContent))
                {
                    return await provider.GetMessageAsync(item.Folder, item.Summary.RemoteId, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Text of {RemoteId} not indexed", item.Summary.RemoteId);
        }

        return null;
    }

    // Mail connections of accounts with indexing on; true where the content is local (read directly).
    private async Task<Dictionary<Guid, bool>> IndexedConnectionsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, bool>();
        foreach (var account in await _accounts.GetAccountsAsync(cancellationToken))
        {
            if (!await IsEnabledAsync(account.Id, cancellationToken))
            {
                continue;
            }

            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                try
                {
                    await using var provider = _providers.CreateMail(connection);
                    result[connection.Id] = provider.Capabilities.HasFlag(MailProviderCapabilities.LocalContent);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException)
                {
                    // provider not available in this build
                }
            }
        }

        return result;
    }

    // ── Per account: on or off (off removes its index) ──

    private static string Key(Guid accountId) => $"search.index.{accountId:N}";

    public async Task<bool> IsEnabledAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        await _settings.GetAsync(Key(accountId), cancellationToken) != "off";

    private static string ServerKey(Guid accountId) => $"search.server.{accountId:N}";

    /// <summary>
    /// Whether accounts fetch the text of mail only on the server unless set otherwise: yes on a computer, no on a
    /// phone (battery and mobile data – there the server's search finds it when asked).
    /// </summary>
    public bool ServerTextByDefault { get; init; } = true;

    /// <summary>Whether the text of mail only on the server is fetched for the index (<see cref="ServerTextByDefault"/> unless set).</summary>
    public async Task<bool> IsServerTextEnabledAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        await _settings.GetAsync(ServerKey(accountId), cancellationToken) switch
        {
            "off" => false,
            "on" => true,
            _ => ServerTextByDefault,
        };

    public async Task SetServerTextEnabledAsync(Account account, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await _settings.SetAsync(ServerKey(account.Id), enabled == ServerTextByDefault ? null : enabled ? "on" : "off", cancellationToken);
        Poke();
        Progress?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetEnabledAsync(Account account, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await _settings.SetAsync(Key(account.Id), enabled ? null : "off", cancellationToken);
        if (!enabled)
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                await _index.ClearSearchIndexAsync(connection.Id, cancellationToken);
            }
        }

        Poke();
        Progress?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every mail account: indexed or not, and how far.</summary>
    public async Task<IReadOnlyList<SearchIndexAccount>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SearchIndexAccount>();
        foreach (var account in await _accounts.GetAccountsAsync(cancellationToken))
        {
            var connections = account.ConnectionsOf(ServiceKind.Mail).ToList();
            if (connections.Count == 0)
            {
                continue;
            }

            var total = new SearchIndexStatus(0, 0, 0);
            foreach (var connection in connections)
            {
                var status = await _index.GetSearchStatusAsync(connection.Id, cancellationToken);
                total = new SearchIndexStatus(total.Messages + status.Messages, total.Indexed + status.Indexed, total.WithText + status.WithText);
            }

            result.Add(new SearchIndexAccount(account, await IsEnabledAsync(account.Id, cancellationToken), total,
                connections.Any(SupportsServerText) ? await IsServerTextEnabledAsync(account.Id, cancellationToken) : null));
        }

        return result;
    }

    /// <summary>Which accounts' mail is in the index (the search uses the index for these, the stored list otherwise).</summary>
    public async Task<HashSet<Guid>> EnabledConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new HashSet<Guid>();
        foreach (var account in await _accounts.GetAccountsAsync(cancellationToken))
        {
            if (await IsEnabledAsync(account.Id, cancellationToken))
            {
                result.UnionWith(account.ConnectionsOf(ServiceKind.Mail).Select(c => c.Id));
            }
        }

        return result;
    }

    /// <summary>
    /// Searching on this device, at once: indexed accounts through the index (sender, subject, recipients, and the
    /// text where it is here), the others through the stored list. Newest first, at most <paramref name="limit"/> hits.
    /// </summary>
    public async Task<MailSearchResult> SearchAsync(IReadOnlyList<MailFolder> folders, MailSearchQuery query, int limit = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(query);
        var enabled = await EnabledConnectionsAsync(cancellationToken);
        var hits = new List<MailSearchHit>();
        var indexed = folders.Where(f => enabled.Contains(f.ConnectionId)).ToList();
        if (indexed.Count > 0 && MatchFor(query) is { } match)
        {
            hits.AddRange(await _index.SearchIndexAsync(match, indexed, limit + 1, cancellationToken));
        }

        foreach (var folder in folders.Where(f => !enabled.Contains(f.ConnectionId)))
        {
            var stored = await _store.GetMessagesAsync(folder.ConnectionId, folder.RemoteId, 0, int.MaxValue, cancellationToken);
            hits.AddRange(stored.Where(query.Matches).Take(limit + 1).Select(m => new MailSearchHit(folder, m)));
        }

        var sorted = hits.OrderByDescending(h => h.Message.Date).ToList();
        return new MailSearchResult([.. sorted.Take(limit)], [], sorted.Count > limit);
    }

    // ── What goes into the index, and how a query is asked ──

    private static SearchDocument Document(MessageSummary summary, string? body, string? attachments) => new(
        summary.From is { } from ? $"{from.Name} {from.Address}" : string.Empty,
        string.Join(" ", summary.To.Select(t => $"{t.Name} {t.Address}")),
        summary.Subject,
        body ?? summary.Preview,
        attachments);

    internal static string? TextOf(MimeMessage message)
    {
        var text = message.TextBody ?? (message.HtmlBody is { } html ? HtmlText.ToPlainText(html) : null);
        return text is { Length: > MaxTextLength } ? text[..MaxTextLength] : text;
    }

    private static string? AttachmentNames(MimeMessage message)
    {
        var names = message.Attachments.Select(a => a is MessagePart { Message.Subject: { } subject } ? subject : a.ContentDisposition?.FileName ?? a.ContentType.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return names.Count == 0 ? null : string.Join(" ", names);
    }

    /// <summary>
    /// The query in the index's syntax: every word must occur (as the start of a word – "Offe" finds "Offerte"), in
    /// the field it was asked for. Null when there is nothing to search for.
    /// </summary>
    public static string? MatchFor(MailSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parts = new List<string>();
        void Add(string? text, string? columns)
        {
            var words = Words(text);
            if (words.Count > 0)
            {
                var expression = string.Join(" AND ", words.Select(w => $"\"{w}\"*"));
                parts.Add(columns is null ? $"({expression})" : $"{columns} : ({expression})");
            }
        }

        Add(query.From, "{Sender}");
        Add(query.Subject, "{Subject}");
        Add(query.Text, "{Body Attachments}");
        Add(query.Anywhere, null);
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    // Letters and digits only (as the index splits words); quotes cannot break out of the phrase.
    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }
}
