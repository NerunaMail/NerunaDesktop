using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Desktop.Infrastructure;

namespace Neruna.Desktop.ViewModels;

/// <summary>Settings → Design: light/dark mode and color scheme; applied immediately.</summary>
internal sealed partial class AppearanceViewModel(ISettingsStore settings, Neruna.Desktop.Infrastructure.UiPreferences preferences, AppOptions options) : ViewModelBase
{
    /// <summary>"Automatisch" (the system's language) and the four languages, each in its own language.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } =
        [new("auto", Neruna.Core.Localization.Texts.T("Automatisch (Systemsprache)")), .. Neruna.Core.Localization.Texts.Languages.Select(l => new LanguageChoice(l.Code, l.Name))];

    [ObservableProperty]
    public partial LanguageChoice? Language { get; set; }

    /// <summary>The language was changed: it applies after a restart.</summary>
    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    partial void OnLanguageChanged(LanguageChoice? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _ = settings.SetAsync(SettingKeys.UiLanguage, value.Code);
        LanguageFile.Write(options.DataDirectory, value.Code);
        RestartNeeded = true;
    }

    [RelayCommand]
    private static void Restart() => LanguageFile.Restart();

    /// <summary>Symbolleiste im Lesebereich: Symbol und Text, or only the symbol (text as tooltip).</summary>
    public bool ToolbarWithText
    {
        get => preferences.ToolbarLabels;
        set
        {
            preferences.ToolbarLabels = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolbarIconsOnly));
        }
    }

    /// <summary>"Konto hinzufügen" in the navigation rail.</summary>
    public bool ShowAddAccountButton
    {
        get => preferences.ShowAddAccountButton;
        set
        {
            preferences.ShowAddAccountButton = value;
            OnPropertyChanged();
        }
    }

    /// <summary>E-Mail, Kalender, Kontakte, Chat in the rail: shown or not, in this order.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NavigationItem> NavigationItems => preferences.NavigationItems;

    [RelayCommand]
    private void MoveUp(NavigationItem item) => preferences.MoveNavigationItem(item, -1);

    [RelayCommand]
    private void MoveDown(NavigationItem item) => preferences.MoveNavigationItem(item, 1);

    public bool ToolbarIconsOnly
    {
        get => !ToolbarWithText;
        set => ToolbarWithText = !value;
    }

    private bool _loading;

    public IReadOnlyList<SchemeChoice> Schemes { get; } =
    [
        new(ColorScheme.Blue, "Blau", "#0F6CBD"),
        new(ColorScheme.Red, "Rot", "#C50F1F"),
        new(ColorScheme.Green, "Grün", "#107C10"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystem), nameof(IsLight), nameof(IsDark))]
    public partial ThemeMode Mode { get; set; }

    [ObservableProperty]
    public partial ColorScheme Scheme { get; set; }

    public bool IsSystem
    {
        get => Mode == ThemeMode.System;
        set => SetMode(value, ThemeMode.System);
    }

    public bool IsLight
    {
        get => Mode == ThemeMode.Light;
        set => SetMode(value, ThemeMode.Light);
    }

    public bool IsDark
    {
        get => Mode == ThemeMode.Dark;
        set => SetMode(value, ThemeMode.Dark);
    }

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            Mode = Enum.TryParse<ThemeMode>(await settings.GetAsync(SettingKeys.ThemeMode), out var m) ? m : ThemeMode.System;
            Scheme = Enum.TryParse<ColorScheme>(await settings.GetAsync(SettingKeys.ColorScheme), out var s) ? s : ColorScheme.Blue;
            var language = await settings.GetAsync(SettingKeys.UiLanguage) ?? "auto";
            Language = LanguageChoices.FirstOrDefault(l => l.Code == language) ?? LanguageChoices[0];
            UpdateSelection();
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void ChooseScheme(SchemeChoice choice) => Scheme = choice.Scheme;

    private void SetMode(bool selected, ThemeMode mode)
    {
        if (selected)
        {
            Mode = mode;
        }
    }

    partial void OnModeChanged(ThemeMode value) => Save(SettingKeys.ThemeMode, value.ToString());

    partial void OnSchemeChanged(ColorScheme value)
    {
        UpdateSelection();
        Save(SettingKeys.ColorScheme, value.ToString());
    }

    private void UpdateSelection()
    {
        foreach (var choice in Schemes)
        {
            choice.IsSelected = choice.Scheme == Scheme;
        }
    }

    private void Save(string key, string value)
    {
        if (_loading)
        {
            return;
        }

        Appearance.Apply(Mode, Scheme);
        _ = settings.SetAsync(key, value);
    }
}

internal sealed partial class SchemeChoice(ColorScheme scheme, string name, string color) : ObservableObject
{
    public ColorScheme Scheme { get; } = scheme;

    public string Name { get; } = name;

    public string Color { get; } = color;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

internal sealed record LanguageChoice(string Code, string Name)
{
    public override string ToString() => Name;
}
