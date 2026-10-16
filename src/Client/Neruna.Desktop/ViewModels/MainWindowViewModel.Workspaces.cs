using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core.Workspaces;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A workspace to switch to, at the top of the rail or as a tab.</summary>
internal sealed record WorkspaceChoice(Workspace Workspace, bool IsCurrent)
{
    public string Name => Workspace.IsFull ? T("Vollansicht") : Workspace.Name;

    /// <summary>First letter on the rail button ("B" for Business); the full view shows a grid icon instead.</summary>
    public string Initial => Workspace.IsFull ? string.Empty : Workspace.Name[..1].ToUpperInvariant();

    public IBrush Brush => Workspace.IsFull ? Brushes.Gray : WorkspaceColors.Brush(Workspace.Color);
}

/// <summary>An entry of the workspace menu ("-" is a separator line).</summary>
internal sealed record WorkspaceMenuItem(string Title, System.Windows.Input.ICommand? Command, object? Parameter, Avalonia.Controls.MenuItemToggleType ToggleType, bool IsChecked);

internal static class WorkspaceColors
{
    /// <summary>Offered in the editor; any #RRGGBB works.</summary>
    public static IReadOnlyList<string> Palette { get; } = ["#0F6CBD", "#0B6A0B", "#C239B3", "#CA5010", "#8764B8", "#038387", "#D13438", "#4F6BED"];

    public static IBrush Brush(string? color) => Color.TryParse(color, out var c) ? new SolidColorBrush(c) : Brushes.Gray;

    /// <summary>The rail in the workspace's colour, light enough for the icons on it.</summary>
    public static IBrush Tint(string? color) => Color.TryParse(color, out var c) ? new SolidColorBrush(c, 0.28) : Brushes.Transparent;
}

/// <summary>
/// Workspaces in the main window: switching (rail icon or tabs), the rail in the workspace's colour, the chat hidden
/// where the workspace says so, and every page reloaded for the new view.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private WorkspaceService? _workspaces;

    public ObservableCollection<WorkspaceChoice> WorkspaceChoices { get; } = [];

    /// <summary>The menu at the top of the rail: the workspaces, then "Workspaces verwalten …".</summary>
    public ObservableCollection<WorkspaceMenuItem> WorkspaceMenu { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorkspaceRail), nameof(ShowWorkspaceTabs))]
    public partial WorkspaceDisplay WorkspaceDisplay { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorkspaceRail), nameof(ShowWorkspaceTabs))]
    public partial bool HasWorkspaces { get; set; }

    public bool ShowWorkspaceRail => HasWorkspaces && WorkspaceDisplay == WorkspaceDisplay.Rail;

    public bool ShowWorkspaceTabs => HasWorkspaces && WorkspaceDisplay == WorkspaceDisplay.Tabs;

    [ObservableProperty]
    public partial WorkspaceChoice? CurrentWorkspace { get; set; }

    /// <summary>Over the rail's background: the open workspace's colour (none in the full view).</summary>
    [ObservableProperty]
    public partial IBrush RailTint { get; set; } = Brushes.Transparent;

    private async Task InitWorkspacesAsync(WorkspaceService workspaces)
    {
        _workspaces = workspaces;
        await workspaces.LoadAsync();
        WorkspaceDisplay = await workspaces.GetDisplayAsync();
        ShowWorkspaces();
        workspaces.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            ShowWorkspaces();
            await ReloadForWorkspaceAsync();
        });
    }

    /// <summary>Einstellungen → Workspaces changed where they are shown.</summary>
    public async Task SetWorkspaceDisplayAsync(WorkspaceDisplay display)
    {
        WorkspaceDisplay = display;
        if (_workspaces is { } workspaces)
        {
            await workspaces.SetDisplayAsync(display);
        }
    }

    [RelayCommand]
    private async Task SwitchWorkspaceAsync(WorkspaceChoice? choice)
    {
        if (choice is not null && _workspaces is { } workspaces)
        {
            await workspaces.SwitchAsync(choice.Workspace.Id);
        }
    }

    [RelayCommand]
    private void ManageWorkspaces()
    {
        Navigate(Section.Settings);
        SettingsPage.ShowWorkspaces();
    }

    private void ShowWorkspaces()
    {
        if (_workspaces is not { } workspaces)
        {
            return;
        }

        var current = workspaces.Current;
        WorkspaceChoices.Clear();
        foreach (var workspace in workspaces.All)
        {
            WorkspaceChoices.Add(new WorkspaceChoice(workspace, workspace.Id == current.Id));
        }

        CurrentWorkspace = WorkspaceChoices.FirstOrDefault(c => c.IsCurrent);
        WorkspaceMenu.Clear();
        foreach (var choice in WorkspaceChoices)
        {
            WorkspaceMenu.Add(new WorkspaceMenuItem(choice.Name, SwitchWorkspaceCommand, choice, MenuItemToggleType.Radio, choice.IsCurrent));
        }

        WorkspaceMenu.Add(new WorkspaceMenuItem("-", null, null, MenuItemToggleType.None, false));
        WorkspaceMenu.Add(new WorkspaceMenuItem(T("Workspaces verwalten …"), ManageWorkspacesCommand, null, MenuItemToggleType.None, false));
        HasWorkspaces = WorkspaceChoices.Count > 1;
        RailTint = current.IsFull ? Brushes.Transparent : WorkspaceColors.Tint(current.Color);

        // The chat button follows the workspace; a chat open in front makes way for the mail.
        foreach (var item in MailPage.Preferences.NavigationItems)
        {
            item.IsHiddenByWorkspace = item.Section == Section.Chat && current.HideChat;
        }

        if (current.HideChat && CurrentPage == ChatPage)
        {
            Navigate(Section.Mail);
        }
    }

    // Everything the open workspace filters is built again; the open folder may no longer be part of it.
    private async Task ReloadForWorkspaceAsync()
    {
        try
        {
            await MailPage.ReloadAsync();
            await CalendarPage.ReloadAsync();
            await TasksPage.ReloadAsync();
            await ContactsPage.ReloadAsync();
            await MailPage.Agenda.ReloadAsync();
            StatusText = CurrentWorkspace is { Workspace.IsFull: false } choice ? F("Workspace «{0}»", choice.Name) : T("Vollansicht");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reloading for the workspace failed");
        }
    }
}
