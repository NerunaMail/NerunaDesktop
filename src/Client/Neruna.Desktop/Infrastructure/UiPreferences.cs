using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Display preferences the views bind to directly; loaded at startup, changed in Einstellungen → Design.</summary>
internal sealed partial class UiPreferences : ObservableObject
{
    private static readonly JsonSerializerOptions NavigationJson = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly ISettingsStore settings;
    private bool _loading;

    public UiPreferences(ISettingsStore settings)
    {
        this.settings = settings;
        LoadNavigation(null);
    }

    /// <summary>Buttons in the reading pane: icon with text below (default), or icon only with the text as tooltip.</summary>
    [ObservableProperty]
    public partial bool ToolbarLabels { get; set; } = true;

    /// <summary>"Konto hinzufügen" at the bottom of the navigation rail.</summary>
    [ObservableProperty]
    public partial bool ShowAddAccountButton { get; set; } = true;

    /// <summary>The upper buttons of the navigation rail in the chosen order; hidden ones stay in the list.</summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; } = [];

    /// <summary>Order or visibility of the rail buttons changed (also after loading).</summary>
    public event EventHandler? NavigationChanged;

    /// <summary>The chat button is shown (hidden: no chat, offline).</summary>
    public bool IsChatShown => NavigationItems.Any(i => i.Section == Section.Chat && i.IsVisible);

    /// <summary>Where Neruna opens: the first visible button.</summary>
    public Section StartSection => NavigationItems.FirstOrDefault(i => i.IsVisible)?.Section ?? Section.Mail;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            ToolbarLabels = await settings.GetBoolAsync(SettingKeys.ToolbarLabels, fallback: true);
            ShowAddAccountButton = await settings.GetBoolAsync(SettingKeys.ShowAddAccountButton, fallback: true);
            LoadNavigation(await settings.GetAsync(SettingKeys.NavigationItems));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>One place up (-1) or down (+1).</summary>
    public void MoveNavigationItem(NavigationItem item, int step)
    {
        var from = NavigationItems.IndexOf(item);
        var to = from + step;
        if (from < 0 || to < 0 || to >= NavigationItems.Count)
        {
            return;
        }

        NavigationItems.Move(from, to);
        SaveNavigation();
    }

    // Saved order and visibility; sections Neruna gained since (e.g. the chat) are added at the end, visible.
    private void LoadNavigation(string? json)
    {
        foreach (var item in NavigationItems)
        {
            item.PropertyChanged -= OnNavigationItemChanged;
        }

        List<NavigationState>? saved = null;
        try
        {
            saved = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<NavigationState>>(json, NavigationJson);
        }
        catch (JsonException)
        {
            // Unreadable: the defaults.
        }

        var defaults = NavigationItem.Defaults().ToDictionary(i => i.Section);
        var ordered = new List<NavigationItem>();
        foreach (var state in saved ?? [])
        {
            if (defaults.Remove(state.Section, out var item))
            {
                item.IsVisible = state.Visible;
                ordered.Add(item);
            }
        }

        ordered.AddRange(defaults.Values);
        if (ordered.All(i => !i.IsVisible))
        {
            ordered[0].IsVisible = true;
        }

        NavigationItems.Clear();
        foreach (var item in ordered)
        {
            item.PropertyChanged += OnNavigationItemChanged;
            NavigationItems.Add(item);
        }

        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNavigationItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NavigationItem.IsVisible) || sender is not NavigationItem item)
        {
            return;
        }

        // At least one stays: a rail without buttons would leave only the settings.
        if (!item.IsVisible && NavigationItems.All(i => !i.IsVisible))
        {
            item.IsVisible = true;
            return;
        }

        SaveNavigation();
        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveNavigation()
    {
        if (!_loading)
        {
            _ = settings.SetAsync(SettingKeys.NavigationItems, JsonSerializer.Serialize(NavigationItems.Select(i => new NavigationState(i.Section, i.IsVisible)).ToList(), NavigationJson));
        }
    }

    private sealed record NavigationState(Section Section, bool Visible);

    partial void OnToolbarLabelsChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(SettingKeys.ToolbarLabels, value);
        }
    }

    partial void OnShowAddAccountButtonChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(SettingKeys.ShowAddAccountButton, value);
        }
    }
}
