using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Display preferences the views bind to directly; loaded at startup, changed in Einstellungen → Design.</summary>
internal sealed partial class UiPreferences(ISettingsStore settings) : ObservableObject
{
    private bool _loading;

    /// <summary>Buttons in the reading pane: icon with text below (default), or icon only with the text as tooltip.</summary>
    [ObservableProperty]
    public partial bool ToolbarLabels { get; set; } = true;

    /// <summary>"Konto hinzufügen" at the bottom of the navigation rail.</summary>
    [ObservableProperty]
    public partial bool ShowAddAccountButton { get; set; } = true;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            ToolbarLabels = await settings.GetBoolAsync(SettingKeys.ToolbarLabels, fallback: true);
            ShowAddAccountButton = await settings.GetBoolAsync(SettingKeys.ShowAddAccountButton, fallback: true);
        }
        finally
        {
            _loading = false;
        }
    }

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
