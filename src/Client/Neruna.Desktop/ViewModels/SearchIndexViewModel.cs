using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → E-Mail → Suchindex: which accounts are indexed (off saves space, its index is removed) and how
/// far the index is.
/// </summary>
internal sealed partial class SearchIndexViewModel : ViewModelBase
{
    private readonly MailSearchIndexer _indexer;
    private DateTime _shownAt;
    private bool _refreshQueued;

    public SearchIndexViewModel(MailSearchIndexer indexer)
    {
        _indexer = indexer;

        // While indexing: the numbers follow, at most once a second.
        indexer.Progress += (_, _) =>
        {
            if (_refreshQueued || Accounts.Count == 0)
            {
                return;
            }

            _refreshQueued = true;
            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _shownAt);
            _ = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero).ContinueWith(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    _refreshQueued = false;
                    await ReloadAsync();
                }),
                TaskScheduler.Default);
        };
    }

    public ObservableCollection<SearchIndexRow> Accounts { get; } = [];

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public async Task ReloadAsync()
    {
        _shownAt = DateTime.UtcNow;
        var status = await Task.Run(() => _indexer.GetStatusAsync());
        if (Accounts.Count != status.Count || Accounts.Zip(status).Any(p => p.First.Account.Id != p.Second.Account.Id))
        {
            Accounts.Clear();
            foreach (var account in status)
            {
                Accounts.Add(new SearchIndexRow(account.Account, enabled => _ = SetEnabledAsync(account.Account, enabled),
                    fromServer => _ = SetServerTextAsync(account.Account, fromServer)));
            }
        }

        foreach (var (row, account) in Accounts.Zip(status))
        {
            row.Update(account);
        }

        IsBusy = _indexer.IsBusy;
        var waiting = status.Where(a => a.IsEnabled).Sum(a => a.Status.Messages - a.Status.Indexed);
        StateText = _indexer.IsBusy || waiting > 0
            ? F("Der Index wird im Hintergrund aufgebaut – noch {0:N0} Nachrichten.", waiting)
            : T("Der Index ist aktuell.");
    }

    private async Task SetServerTextAsync(Account account, bool enabled)
    {
        await _indexer.SetServerTextEnabledAsync(account, enabled);
        await ReloadAsync();
    }

    private async Task SetEnabledAsync(Account account, bool enabled)
    {
        await _indexer.SetEnabledAsync(account, enabled);
        await ReloadAsync();
    }
}

/// <summary>One account on the index page: on or off, and how far.</summary>
internal sealed partial class SearchIndexRow(Account account, Action<bool> changed, Action<bool> serverTextChanged) : ObservableObject
{
    private bool _updating;

    public Account Account { get; } = account;

    public string Title => Account.Title;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    /// <summary>Share of messages in the index (0–100).</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary>The text of mail only on the server is fetched for the index (IMAP).</summary>
    [ObservableProperty]
    public partial bool ServerText { get; set; }

    /// <summary>The account's server can give the text for the index.</summary>
    [ObservableProperty]
    public partial bool CanFetchServerText { get; set; }

    partial void OnServerTextChanged(bool value)
    {
        if (!_updating)
        {
            serverTextChanged(value);
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_updating)
        {
            changed(value);
        }
    }

    public void Update(SearchIndexAccount state)
    {
        _updating = true;
        IsEnabled = state.IsEnabled;
        ServerText = state.ServerText == true;
        CanFetchServerText = state.IsEnabled && state.ServerText is not null;
        _updating = false;
        var status = state.Status;
        Percent = status.Messages == 0 ? 100 : 100.0 * status.Indexed / status.Messages;
        Details = !state.IsEnabled
            ? T("Nicht im Index – die Suche geht über die Liste der Nachrichten und den Server.")
            : F("{0:N0} von {1:N0} Nachrichten im Index, davon {2:N0} mit Text.", status.Indexed, status.Messages, status.WithText);
    }
}
