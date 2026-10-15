using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>What the search needs from the mail page: its folders, the open one, the quick search term, the list.</summary>
internal interface IMailSearchHost
{
    IReadOnlyList<MailAccountNode> SearchAccounts { get; }

    MailFolderNode? CurrentFolder { get; }

    string SearchText { get; }

    /// <summary>Shows the hits instead of the folder.</summary>
    /// <param name="keepTerm">The quick search term the server searched for: it stays in the search box.</param>
    void ShowSearchResults(IReadOnlyList<MessageItemViewModel> hits, string title, string? keepTerm);

    void ReportSearchFailure(Exception error);

    void ShowStatus(string message);

    /// <summary>The quick search was emptied: back to the folder.</summary>
    void EndQuickSearch();

    /// <summary>Whether the search shows results right now.</summary>
    bool IsSearchMode { get; }
}

/// <summary>
/// Searching: while typing, the local index answers at once (the open folder, or all folders); Enter or "Auch auf
/// dem Server suchen" adds what only the server knows (the text of mail not on this device). The advanced search
/// (sender, subject, text; a folder or all folders of an account) asks both. The mail page shows the hits.
/// </summary>
internal sealed partial class MailSearchViewModel(MailController mail, MailSearchIndexer index, IMailSearchHost host) : ViewModelBase, IDisposable
{
    public void Dispose() => _running?.Dispose();

    private int _typing;

    // The quick search goes over all folders (after "In allen Ordnern").
    private bool _everywhere;

    /// <summary>The last results came from this device only: the server can still be asked.</summary>
    [ObservableProperty]
    public partial bool CanAskServer { get; set; }

    /// <summary>The quick search shows the open folder: it can be widened to all folders.</summary>
    [ObservableProperty]
    public partial bool CanSearchEverywhere { get; set; }

    /// <summary>Typed into the search box: after a short pause the index is asked (at least 2 characters).</summary>
    public async Task TypedAsync(string text)
    {
        var term = text.Trim();
        if (term == ServerTerm)
        {
            return; // the box shows the term of the results on screen
        }

        // Only the last keystroke of a short pause searches.
        var typing = ++_typing;
        await Task.Delay(250);
        if (typing != _typing)
        {
            return;
        }

        if (term.Length < 2)
        {
            if (host.IsSearchMode && ServerTerm is not null)
            {
                host.EndQuickSearch();
            }

            return;
        }

        await QuickAsync(term, server: false);
    }

    private Task QuickAsync(string term, bool server)
    {
        var folders = _everywhere || host.CurrentFolder is null
            ? host.SearchAccounts.SelectMany(a => a.AllFolders()).ToList()
            : [host.CurrentFolder];
        var where = _everywhere || host.CurrentFolder is null ? T("allen Ordnern") : host.CurrentFolder.Name;
        CanSearchEverywhere = !_everywhere && host.CurrentFolder is not null;
        return SearchAsync(new MailSearchQuery(Anywhere: term), folders, where, keepTerm: term, server: server);
    }

    /// <summary>"In allen Ordnern": the quick search term in every folder of every account.</summary>
    [RelayCommand]
    private Task SearchEverywhereAsync()
    {
        _everywhere = true;
        return host.SearchText.Trim() is { Length: > 0 } term ? QuickAsync(term, server: false) : Task.CompletedTask;
    }
    /// <summary>"Erweiterte Suche" is open above the list.</summary>
    [ObservableProperty]
    public partial bool IsAdvancedSearchOpen { get; set; }

    [ObservableProperty]
    public partial string SearchFrom { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchSubject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchBody { get; set; } = string.Empty;

    /// <summary>Where to search: a folder, or all folders of an account.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SearchScope> SearchScopes { get; set; } = [];

    [ObservableProperty]
    public partial SearchScope? SearchScopeChoice { get; set; }

    [ObservableProperty]
    public partial bool SearchSubfolders { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSearchCommand), nameof(SearchServerCommand))]
    public partial bool IsSearching { get; set; }

    /// <summary>"23 Treffer in Posteingang und Unterordnern" above the results.</summary>
    [ObservableProperty]
    public partial string? SearchInfo { get; set; }

    /// <summary>
    /// The term the server already searched for: it stays in the search box, but does not filter the results again
    /// (hits found in the message text would vanish). A changed term filters the results as usual.
    /// </summary>
    public string? ServerTerm { get; private set; }

    // The toggle button sets IsAdvancedSearchOpen itself (it must not also run a toggling command: that closed the
    // panel again in the same click).
    partial void OnIsAdvancedSearchOpenChanged(bool value)
    {
        if (value)
        {
            SearchScopes = SearchScope.For(host.SearchAccounts);
            SearchScopeChoice = SearchScopes.FirstOrDefault(s => s.Folder is { } f && f == host.CurrentFolder) ?? SearchScopes.FirstOrDefault();
            if (SearchBody.Length == 0 && SearchFrom.Length == 0 && SearchSubject.Length == 0)
            {
                SearchBody = host.SearchText.Trim();
            }
        }
    }

    /// <summary>"Suchen" in the advanced search: sender, subject and text in the chosen folders, on the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task RunSearchAsync()
    {
        if (SearchScopeChoice is not { } scope)
        {
            return Task.CompletedTask;
        }

        var query = new MailSearchQuery(Clean(SearchFrom), Clean(SearchSubject), Clean(SearchBody));
        CanSearchEverywhere = false;
        return SearchAsync(query, scope.Folders(SearchSubfolders), scope.Describe(SearchSubfolders));
    }

    /// <summary>
    /// Enter or "Auch auf dem Server suchen": the last search (quick or advanced) once more, now also on the server (the
    /// text of mail not on this device); in the quick search box a changed term is searched anew.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchServerAsync()
    {
        if (_last is { } last && (last.KeepTerm is null || last.KeepTerm == host.SearchText.Trim()))
        {
            return SearchAsync(last.Query, last.Folders, last.Where, last.KeepTerm, server: true);
        }

        return host.SearchText.Trim() is { Length: > 0 } term ? QuickAsync(term, server: true) : Task.CompletedTask;
    }

    // The search on screen, to ask the server for it too.
    private (MailSearchQuery Query, IReadOnlyList<MailFolderNode> Folders, string Where, string? KeepTerm)? _last;

    private CancellationTokenSource? _running;

    /// <summary>"Abbrechen" while searching: what was found so far stays.</summary>
    [RelayCommand]
    private void CancelSearch() => _running?.Cancel();

    /// <summary>Back to the folder: the search box keeps a term only if the user changed it since.</summary>
    /// <returns>Whether the search box should be emptied (it still holds the term the server searched for).</returns>
    public bool Close()
    {
        IsAdvancedSearchOpen = false;
        SearchInfo = null;
        _everywhere = false;
        CanAskServer = false;
        CanSearchEverywhere = false;
        var clearBox = ServerTerm is not null && host.SearchText.Trim() == ServerTerm;
        ServerTerm = null;
        return clearBox;
    }

    // A new search replaces a running one.
    private static bool CanSearch() => true;

    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task SearchAsync(MailSearchQuery query, IReadOnlyList<MailFolderNode> folders, string where, string? keepTerm = null, bool server = false)
    {
        if (query.IsEmpty)
        {
            host.ShowStatus(T("Bitte mindestens einen Suchbegriff eingeben."));
            return;
        }

        _last = (query, folders, where, keepTerm);
        if (_running is { } previous)
        {
            await previous.CancelAsync();
        }

        using var running = _running = new CancellationTokenSource();
        var cancel = running.Token;
        IsSearching = true;
        IReadOnlyList<MailSearchHit> shown = [];
        try
        {
            var nodes = folders.ToDictionary(f => (f.Folder.ConnectionId, f.Folder.RemoteId));
            var searched = folders.Select(f => f.Folder).ToList();
            var several = folders.Select(f => f.Folder.ConnectionId).Distinct().Count() > 1;
            MessageItemViewModel Item(MailSearchHit hit)
            {
                var node = nodes[(hit.Folder.ConnectionId, hit.Folder.RemoteId)];
                return new MessageItemViewModel(hit.Message, node)
                {
                    FolderText = folders.Count > 1 ? (several ? $"{node.Name} · {node.Account.Title}" : node.Name) : null,
                };
            }

            void Show(IReadOnlyList<MailSearchHit> hits, bool truncated, int failed, bool more)
            {
                shown = hits;
                ServerTerm = keepTerm;
                host.ShowSearchResults([.. hits.Select(Item)], F("Suche in {0}", where), keepTerm);
                SearchInfo = (hits.Count == 1 ? T("1 Treffer") : F("{0} Treffer", hits.Count))
                             + (truncated ? T(" – nur die neuesten, bitte genauer suchen") : string.Empty)
                             + (failed > 0 ? F(" – {0} Ordner konnten nicht durchsucht werden", failed) : string.Empty)
                             + (more ? T(" – Server wird durchsucht …") : string.Empty);
            }

            // On this device first: at once.
            var local = await Task.Run(() => index.SearchAsync(searched, query, cancellationToken: cancel), cancel);
            CanAskServer = !server && folders.Any(f => !f.Account.IsReadOnly); // an Outlook file has no server
            Show(local.Hits, local.IsTruncated, 0, server);
            if (!server)
            {
                return;
            }

            // Then the server: hits only it knows join in (the same message is not shown twice).
            var remote = await Task.Run(() => mail.SearchAsync(searched, query, cancellationToken: cancel), cancel);
            var seen = local.Hits.Select(h => (h.Folder.ConnectionId, h.Folder.RemoteId, h.Message.RemoteId)).ToHashSet();
            var merged = local.Hits.Concat(remote.Hits.Where(h => seen.Add((h.Folder.ConnectionId, h.Folder.RemoteId, h.Message.RemoteId))))
                .OrderByDescending(h => h.Message.Date).ToList();
            Show(merged, local.IsTruncated || remote.IsTruncated, remote.FailedFolders.Count, false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled (or replaced by a newer search, which says its own): the hits so far stay.
            if (_running == running)
            {
                SearchInfo = (shown.Count == 1 ? T("1 Treffer") : F("{0} Treffer", shown.Count)) + T(" – Suche abgebrochen");
                CanAskServer = folders.Any(f => !f.Account.IsReadOnly);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            host.ReportSearchFailure(ex);
            SearchInfo = T("Suche fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            if (_running == running)
            {
                _running = null;
                IsSearching = false;
            }
        }
    }
}
