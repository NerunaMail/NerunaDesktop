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
}

/// <summary>
/// Searching on the server: the quick search term in the whole open folder ("Im ganzen Ordner suchen") and the
/// advanced search (sender, subject, text; a folder or all folders of an account). The mail page shows the hits.
/// </summary>
internal sealed partial class MailSearchViewModel(MailController mail, IMailSearchHost host) : ViewModelBase
{
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
        return SearchAsync(query, scope.Folders(SearchSubfolders), scope.Describe(SearchSubfolders));
    }

    /// <summary>The quick search term in the whole open folder on the server (also the text of messages).</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchServerAsync() =>
        host.CurrentFolder is { } folder && host.SearchText.Trim() is { Length: > 0 } term
            ? SearchAsync(new MailSearchQuery(Anywhere: term), [folder], folder.Name, keepTerm: term)
            : Task.CompletedTask;

    /// <summary>Back to the folder: the search box keeps a term only if the user changed it since.</summary>
    /// <returns>Whether the search box should be emptied (it still holds the term the server searched for).</returns>
    public bool Close()
    {
        IsAdvancedSearchOpen = false;
        SearchInfo = null;
        var clearBox = ServerTerm is not null && host.SearchText.Trim() == ServerTerm;
        ServerTerm = null;
        return clearBox;
    }

    private bool CanSearch() => !IsSearching;

    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task SearchAsync(MailSearchQuery query, IReadOnlyList<MailFolderNode> folders, string where, string? keepTerm = null)
    {
        if (query.IsEmpty)
        {
            host.ShowStatus(T("Bitte mindestens einen Suchbegriff eingeben."));
            return;
        }

        IsSearching = true;
        SearchInfo = T("Suche läuft …");
        try
        {
            var nodes = folders.ToDictionary(f => (f.Folder.ConnectionId, f.Folder.RemoteId));
            var searched = folders.Select(f => f.Folder).ToList();
            var result = await Task.Run(() => mail.SearchAsync(searched, query));
            var several = folders.Select(f => f.Folder.ConnectionId).Distinct().Count() > 1;
            var hits = result.Hits.Select(hit =>
            {
                var node = nodes[(hit.Folder.ConnectionId, hit.Folder.RemoteId)];
                return new MessageItemViewModel(hit.Message, node)
                {
                    FolderText = folders.Count > 1 ? (several ? $"{node.Name} · {node.Account.Title}" : node.Name) : null,
                };
            }).ToList();

            ServerTerm = keepTerm;
            host.ShowSearchResults(hits, F("Suche in {0}", where), keepTerm);
            SearchInfo = (hits.Count == 1 ? T("1 Treffer") : F("{0} Treffer", hits.Count))
                         + (result.IsTruncated ? T(" – nur die neuesten pro Ordner, bitte genauer suchen") : string.Empty)
                         + (result.FailedFolders.Count > 0 ? F(" – {0} Ordner konnten nicht durchsucht werden", result.FailedFolders.Count) : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            host.ReportSearchFailure(ex);
            SearchInfo = T("Suche fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }
}
