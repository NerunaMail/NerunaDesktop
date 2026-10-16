using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.News;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A post in Einstellungen → News: teaser first, the whole text on request.</summary>
internal sealed partial class NewsItem(NewsPost post) : ObservableObject
{
    public NewsPost Post { get; } = post;

    public string Title => Post.Title;

    public string DateText => Post.Date == DateOnly.MinValue ? string.Empty : Post.Date.ToString("D", System.Globalization.CultureInfo.CurrentCulture);

    public string Summary => Post.Summary;

    public IReadOnlyList<NewsBlock> Blocks => Post.Blocks;

    public Uri Url => Post.Url;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// Einstellungen → Programm → News: the devblog of neruna.org – what's new, what got better. Loaded when the page is
/// opened (once per start, again with "Aktualisieren").
/// </summary>
internal sealed partial class NewsViewModel(DevblogFeed feed) : ViewModelBase
{
    public ObservableCollection<NewsItem> Posts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string? Error { get; set; }

    public bool IsEmpty => !IsLoading && Error is null && Posts.Count == 0;

    public static Uri BlogUri => SettingsViewModel.DevblogUri;

    private bool _loaded;

    /// <summary>Opening the page: the feed, unless it was read already.</summary>
    public Task ShowAsync() => _loaded ? Task.CompletedTask : RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            Show(await feed.GetAsync(Language));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Error = T("Die News konnten nicht geladen werden – keine Verbindung zu neruna.org?");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The posts to show (also for the screenshots).</summary>
    public void Show(IReadOnlyList<NewsPost> posts)
    {
        ArgumentNullException.ThrowIfNull(posts);
        _loaded = true;
        Posts.Clear();
        foreach (var post in posts)
        {
            Posts.Add(new NewsItem(post));
        }

        if (Posts.Count > 0)
        {
            Posts[0].IsExpanded = true; // the newest one is what one came for
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool CanRefresh() => !IsLoading;
}
